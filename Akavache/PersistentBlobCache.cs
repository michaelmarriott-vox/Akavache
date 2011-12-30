﻿using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.IsolatedStorage;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Text;
using System.Threading;
using ReactiveUI;

namespace Akavache
{
    /// <summary>
    /// This class represents an asynchronous key-value store backed by a 
    /// directory. It stores the last 'n' key requests in memory.
    /// </summary>
    public abstract class PersistentBlobCache : IBlobCache, IEnableLogger
    {
        protected MemoizingMRUCache<string, AsyncSubject<byte[]>> MemoizedRequests;
        protected readonly string CacheDirectory;
        protected ConcurrentDictionary<string, DateTimeOffset> CacheIndex = new ConcurrentDictionary<string, DateTimeOffset>();
        readonly Subject<Unit> actionTaken = new Subject<Unit>();
        protected IFilesystemProvider filesystem;

        public IScheduler Scheduler { get; protected set; }

        const string BlobCacheIndexKey = "__THISISTHEINDEX__FFS_DONT_NAME_A_FILE_THIS™";
        const char UnicodeSeparator = '\u2029'; // PARAGRAPH SEPARATOR PSEP

        protected PersistentBlobCache(string cacheDirectory = null, IFilesystemProvider filesystemProvider = null, IScheduler scheduler = null)
        {
            this.CacheDirectory = cacheDirectory ?? GetDefaultRoamingCacheDirectory();
            this.Scheduler = scheduler ?? RxApp.TaskpoolScheduler;
            this.filesystem = filesystemProvider ?? new SimpleFilesystemProvider();

            // Here, we're not actually caching the requests directly (i.e. as
            // byte[]s), but as the "replayed result of the request", in the
            // AsyncSubject - this makes the code infinitely simpler because
            // we don't have to keep a separate list of "in-flight reads" vs
            // "already completed and cached reads"
            MemoizedRequests = new MemoizingMRUCache<string, AsyncSubject<byte[]>>(
                (x,c) => FetchOrWriteBlobFromDisk(x,c,false), 20);
                
            filesystem.CreateRecursive(CacheDirectory);

            FetchOrWriteBlobFromDisk(BlobCacheIndexKey, null, true)
                .Catch(Observable.Return(new byte[0]))
                .Select(x => Encoding.UTF8.GetString(x, 0, x.Length).Split('\n')
                    .SelectMany(ParseCacheIndexEntry)
                    .ToDictionary(y => y.Key, y => y.Value))
                .Select(x => new ConcurrentDictionary<string, DateTimeOffset>(x))
                .Subscribe(x => CacheIndex = x);

            actionTaken
                .Throttle(TimeSpan.FromSeconds(2), RxApp.TaskpoolScheduler)
                .Subscribe(_ => FlushCacheIndex(false));

            this.Log().InfoFormat("{0} entries in blob cache index", CacheIndex.Count);
        }


        static readonly Lazy<IBlobCache> _LocalMachine = new Lazy<IBlobCache>(() => new CPersistentBlobCache(GetDefaultLocalMachineCacheDirectory()));
        public static IBlobCache LocalMachine 
        {
            get { return _LocalMachine.Value;  }
        }

        static readonly Lazy<IBlobCache> _UserAccount = new Lazy<IBlobCache>(() => new CPersistentBlobCache(GetDefaultRoamingCacheDirectory()));
        public static IBlobCache UserAccount 
        {
            get { return _UserAccount.Value;  }
        }

        class CPersistentBlobCache : PersistentBlobCache {
            public CPersistentBlobCache(string cacheDirectory) : base(cacheDirectory, null, RxApp.TaskpoolScheduler) { }
        }

        public void Insert(string key, byte[] data, DateTimeOffset? absoluteExpiration = null)
        {
            if (key == null || data == null)
            {
                throw new ArgumentNullException();
            }

            // NB: Since FetchOrWriteBlobFromDisk is guaranteed to not block,
            // we never sit on this lock for any real length of time
            lock(MemoizedRequests)
            {
                MemoizedRequests.Invalidate(key);
                var err = MemoizedRequests.Get(key, data);

                // If we fail trying to fetch/write the key on disk, we want to 
                // try again instead of replaying the same failure
                err.LogErrors("Insert").Subscribe(
                    x => CacheIndex[key] = absoluteExpiration ?? DateTimeOffset.MaxValue, 
                    ex => Invalidate(key));
                
                actionTaken.OnNext(Unit.Default);
            }
        }

        public IObservable<byte[]> GetAsync(string key)
        {
            lock(MemoizedRequests)
            {
                IObservable<byte[]> ret;
                if (IsKeyStale(key))
                {
                    Invalidate(key);
                    ret = Observable.Throw<byte[]>(new KeyNotFoundException());
                    goto leave;
                }

                // There are three scenarios here, and we handle all of them 
                // with aplomb and elegance:
                //
                // 1. The key is already in memory as a completed request - we 
                //     return the AsyncSubject which will replay the result
                //
                // 2. The key is currently being fetched from disk - in this
                //     case, MemoizingMRUCache has an AsyncSubject for it (since
                //     FetchOrWriteBlobFromDisk completes immediately), and the
                //     client will get the result when the disk I/O completes
                //
                // 3. The key isn't in memory and isn't being fetched - in
                //    this case, FetchOrWriteBlobFromDisk will be called which
                //    will immediately return an AsyncSubject representing the
                //    queued disk read.
                ret = MemoizedRequests.Get(key);

                // If we fail trying to fetch/write the key on disk, we want to 
                // try again instead of replaying the same failure
                ret.LogErrors("GetAsync")
                    .Subscribe(x => {}, ex => Invalidate(key)); 

            leave:
                actionTaken.OnNext(Unit.Default);
                return ret;
            }
        }

        bool IsKeyStale(string key)
        {
            DateTimeOffset value;
            return (CacheIndex.TryGetValue(key, out value) && value < Scheduler.Now);
        }

        public IEnumerable<string> GetAllKeys()
        {
            lock (MemoizedRequests)
            {
                actionTaken.OnNext(Unit.Default);
                return CacheIndex.Keys.ToArray();
            }
        }

        public void Invalidate(string key)
        {
            Action deleteMe;
            lock(MemoizedRequests)
            {
                this.Log().DebugFormat("Invalidating {0}", key);
                MemoizedRequests.Invalidate(key);

                DateTimeOffset dontcare;
                CacheIndex.TryRemove(key, out dontcare);

                var path = GetPathForKey(key);
                deleteMe = () =>
                {
                    try
                    {
                        filesystem.Delete(path);
                    }
                    catch (FileNotFoundException ex) { this.Log().Warn(ex); }
                    catch (IsolatedStorageException ex) { this.Log().Warn(ex); }
                };

                actionTaken.OnNext(Unit.Default);
            }
                
	    deleteMe.Retry();
        }

        public void InvalidateAll()
        {
            lock(MemoizedRequests)
            {
                foreach(var key in CacheIndex.Keys.ToArray())
                {
                    Invalidate(key);
                }
            }
        }

        public void Dispose()
        {
            // We need to make sure that all outstanding writes are flushed
            // before we bail
            AsyncSubject<byte[]>[] requests;
            lock(MemoizedRequests)
            {
                requests = MemoizedRequests.CachedValues().ToArray();
                MemoizedRequests = null;
            }

            if (requests.Length > 0)
            {
                // Since these are all AsyncSubjects, most of them will replay
                // immediately, except for the ones still outstanding; we'll 
                // Merge them all then wait for them all to complete.
                requests.Merge()
                    .Timeout(TimeSpan.FromSeconds(30), Scheduler)
                    .Wait();
            }

            FlushCacheIndex(true).Subscribe(_ => {});
        }

        /// <summary>
        /// This method is called immediately before writing any data to disk.
        /// Override this in encrypting data stores in order to encrypt the
        /// data.
        /// </summary>
        /// <param name="data">The byte data about to be written to disk.</param>
        /// <param name="scheduler">The scheduler to use if an operation has
        /// to be deferred. If the operation can be done immediately, use
        /// Observable.Return and ignore this parameter.</param>
        /// <returns>A Future result representing the encrypted data</returns>
        protected virtual IObservable<byte[]> BeforeWriteToDiskFilter(byte[] data, IScheduler scheduler)
        {
            return Observable.Return(data);
        }

        /// <summary>
        /// This method is called immediately after reading any data to disk.
        /// Override this in encrypting data stores in order to decrypt the
        /// data.
        /// </summary>
        /// <param name="data">The byte data that has just been read from
        /// disk.</param>
        /// <param name="scheduler">The scheduler to use if an operation has
        /// to be deferred. If the operation can be done immediately, use
        /// Observable.Return and ignore this parameter.</param>
        /// <returns>A Future result representing the decrypted data</returns>
        protected virtual IObservable<byte[]> AfterReadFromDiskFilter(byte[] data, IScheduler scheduler)
        {
            return Observable.Return(data);
        }

        AsyncSubject<byte[]> FetchOrWriteBlobFromDisk(string key, object byteData, bool synchronous)
        {
            // If this is secretly a write, dispatch to WriteBlobToDisk (we're 
            // kind of abusing the 'context' variable from MemoizingMRUCache 
            // here a bit)
            if (byteData != null)
            {
                return WriteBlobToDisk(key, (byte[]) byteData, synchronous);
            }

            var ret = new AsyncSubject<byte[]>();
            var ms = new MemoryStream();

            var scheduler = synchronous ? System.Reactive.Concurrency.Scheduler.Immediate : Scheduler;
            filesystem.SafeOpenFileAsync(GetPathForKey(key), FileMode.Open, FileAccess.Read, FileShare.Read)
                .SelectMany(x => x.CopyToAsync(ms, scheduler))
                .SelectMany(x => AfterReadFromDiskFilter(ms.ToArray(), scheduler))
                .Catch<byte[], FileNotFoundException>(ex => Observable.Throw<byte[]>(new KeyNotFoundException()))
                .Catch<byte[], IsolatedStorageException>(ex => Observable.Throw<byte[]>(new KeyNotFoundException()))
                .Multicast(ret).Connect();

            return ret;
        }

        AsyncSubject<byte[]> WriteBlobToDisk(string key, byte[] byteData, bool synchronous)
        {
            var ret = new AsyncSubject<byte[]>();
            var scheduler = synchronous ? System.Reactive.Concurrency.Scheduler.Immediate : Scheduler;

            var files = Observable.Zip(
                BeforeWriteToDiskFilter(byteData, scheduler).Select(x => new MemoryStream(x)),
                filesystem.SafeOpenFileAsync(GetPathForKey(key), FileMode.Create, FileAccess.Write, FileShare.None),
                (from, to) => new { from, to }
            );

            // NB: The fact that our writing AsyncSubject waits until the 
            // write actually completes means that an Insert immediately 
            // followed by a Get will take longer to process - however,
            // this also means that failed writes will disappear from the
            // cache, which is A Good Thing.
            files
                .SelectMany(x => x.from.CopyToAsync(x.to, scheduler))
                .Select(_ => byteData)
                .Multicast(ret).Connect();
    
            return ret;
        }

        IObservable<Unit> FlushCacheIndex(bool synchronous)
        {
            var index = CacheIndex.Select(x => 
                String.Format(CultureInfo.InvariantCulture, "{0}{3}{1}{3}{2}", x.Key, x.Value.Ticks, x.Value.Offset.Ticks, UnicodeSeparator));

            return WriteBlobToDisk(BlobCacheIndexKey, Encoding.UTF8.GetBytes(String.Join("\n", index)), synchronous)
                .Select(_ => Unit.Default);
        }

        IEnumerable<KeyValuePair<string, DateTimeOffset>> ParseCacheIndexEntry(string s)
        {
            if (String.IsNullOrWhiteSpace(s))
            {
                return Enumerable.Empty<KeyValuePair<string, DateTimeOffset>>();
            }

            try
            {
                var parts = s.Split(UnicodeSeparator);
                var time = new DateTimeOffset(
                    Int64.Parse(parts[1], CultureInfo.InvariantCulture),
                    new TimeSpan(Int64.Parse(parts[2], CultureInfo.InvariantCulture)));

                return new[] {new KeyValuePair<string, DateTimeOffset>(parts[0], time)};
            } 
            catch(Exception ex)
            {
                this.Log().Warn("Invalid cache index entry", ex);
                return Enumerable.Empty<KeyValuePair<string, DateTimeOffset>>();
            }
        }

        string GetPathForKey(string key)
        {
            return Path.Combine(CacheDirectory, Utility.GetMd5Hash(key));
        }

        protected static string GetDefaultRoamingCacheDirectory()
        {
            return RxApp.InUnitTestRunner() ?
                Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "BlobCache") :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), BlobCache.ApplicationName, "BlobCache");
        }

        protected static string GetDefaultLocalMachineCacheDirectory()
        {
            return RxApp.InUnitTestRunner() ?
                Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "LocalBlobCache") :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), BlobCache.ApplicationName, "BlobCache");
        }
    }
}
