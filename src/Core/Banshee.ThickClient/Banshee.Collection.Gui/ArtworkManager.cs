//
// ArtworkManager.cs
//
// Author:
//   Aaron Bockover <abockover@novell.com>
//
// Copyright (C) 2007-2008 Novell, Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using Mono.Unix;

using Gdk;

using Hyena;
using Hyena.Gui;
using Hyena.Collections;

using Banshee.Base;
using Banshee.IO;
using Banshee.ServiceStack;

namespace Banshee.Collection.Gui
{
    public class ArtworkManager : IService
    {
        private Dictionary<int, SurfaceCache> scale_caches  = new Dictionary<int, SurfaceCache> ();
        private HashSet<int> cacheable_cover_sizes = new HashSet<int> ();

        private class SurfaceCache : LruCache<string, Cairo.ImageSurface>
        {
            public SurfaceCache (int max_items) : base (max_items)
            {
            }

            protected override void ExpireItem (Cairo.ImageSurface item)
            {
                if (item != null) {
                    ((IDisposable)item).Dispose ();
                }
            }
        }

        public ArtworkManager ()
        {
            AddCachedSize (36);
            AddCachedSize (40);
            AddCachedSize (42);
            AddCachedSize (48);
            AddCachedSize (64);
            AddCachedSize (300);

            try {
                MigrateCacheDir ();
            } catch (Exception e) {
                Log.Exception ("Could not migrate album artwork cache directory", e);
            }
        }

        public Cairo.ImageSurface LookupSurface (string id)
        {
            return LookupScaleSurface (id, 0);
        }

        public Cairo.ImageSurface LookupScaleSurface (string id, int size)
        {
            return LookupScaleSurface (id, size, false);
        }

        public Cairo.ImageSurface LookupScaleSurface (string id, int size, bool useCache)
        {
            SurfaceCache cache = null;
            Cairo.ImageSurface surface = null;

            if (id == null) {
                return null;
            }

            if (useCache && scale_caches.TryGetValue (size, out cache) && cache.TryGetValue (id, out surface)) {
                return surface;
            }

            Pixbuf pixbuf = LookupScalePixbuf (id, size);
            if (pixbuf == null || pixbuf.Handle == IntPtr.Zero) {
                return null;
            }

            try {
                surface = PixbufImageSurface.Create (pixbuf);
                if (surface == null) {
                    return null;
                }

                if (!useCache) {
                    return surface;
                }

                if (cache == null) {
                    int bytes = 4 * size * size;
                    int max = (1 << 20) / bytes;

                    Log.DebugFormat ("Creating new surface cache for {0}px, {1} KB (max) images, capped at 1 MB ({2} items)",
                        size, bytes, max);

                    cache = new SurfaceCache (max);
                    scale_caches.Add (size, cache);
                }

                cache.Add (id, surface);
                return surface;
            } finally {
                DisposePixbuf (pixbuf);
            }
        }

        public Pixbuf LookupPixbuf (string id)
        {
            return LookupScalePixbuf (id, 0);
        }

        public Pixbuf LookupScalePixbuf (string id, int size)
        {
            if (id == null || (size != 0 && size < 10)) {
                return null;
            }

            // Find the scaled, cached file
            string path = CoverArtSpec.GetPathForSize (id, size);
            if (File.Exists (new SafeUri (path))) {
                try {
                    return new Pixbuf (path);
                } catch {
                    return null;
                }
            }

            string orig_path = CoverArtSpec.GetPathForSize (id, 0);
            bool orig_exists = File.Exists (new SafeUri (orig_path));

            if (!orig_exists) {
                // It's possible there is an image with extension .cover that's waiting
                // to be converted into a jpeg
                string unconverted_path = System.IO.Path.ChangeExtension (orig_path, "cover");
                if (File.Exists (new SafeUri (unconverted_path))) {
                    try {
                        Pixbuf pixbuf = new Pixbuf (unconverted_path);
                        if (pixbuf.Width < 50 || pixbuf.Height < 50) {
                            Hyena.Log.DebugFormat ("Ignoring cover art {0} because less than 50x50", unconverted_path);
                            return null;
                        }

                        pixbuf.Save (orig_path, "jpeg");
                        orig_exists = true;
                    } catch {
                    } finally {
                        File.Delete (new SafeUri (unconverted_path));
                    }
                }
            }

            if (orig_exists && size >= 10) {
                try {
                    Pixbuf pixbuf = new Pixbuf (orig_path);
                    Pixbuf scaled_pixbuf = pixbuf.ScaleSimple (size, size, Gdk.InterpType.Bilinear);

                    if (IsCachedSize (size)) {
                        Directory.Create (System.IO.Path.GetDirectoryName (path));
                        scaled_pixbuf.Save (path, "jpeg");
                    } else {
                        Log.InformationFormat ("Uncached artwork size {0} requested", size);
                    }

                    DisposePixbuf (pixbuf);
                    return scaled_pixbuf;
                } catch {}
            }

            return null;
        }

        public void AddCachedSize (int size)
        {
            cacheable_cover_sizes.Add (size);
        }

        public bool IsCachedSize (int size)
        {
            return cacheable_cover_sizes.Contains (size);
        }

        public IEnumerable<int> CachedSizes ()
        {
            return cacheable_cover_sizes;
        }

        private static int dispose_count = 0;
        public static void DisposePixbuf (Pixbuf pixbuf)
        {
            if (pixbuf != null && pixbuf.Handle != IntPtr.Zero) {
                pixbuf.Dispose ();
                pixbuf = null;

                // There is an issue with disposing Pixbufs where we need to explicitly
                // call the GC otherwise it doesn't get done in a timely way.  But if we
                // do it every time, it slows things down a lot; so only do it every 100th.
                if (++dispose_count % 100 == 0) {
                    System.GC.Collect ();
                    dispose_count = 0;
                }
            }
        }

        string IService.ServiceName {
            get { return "ArtworkManager"; }
        }

#region Cache Directory Versioning/Migration

        private const int CUR_VERSION = 2;
        private void MigrateCacheDir ()
        {
            int version = CacheVersion;
            if (version == CUR_VERSION) {
                return;
            }

            var root_path = CoverArtSpec.RootPath;

            if (version < 1) {
                string legacy_artwork_path = Paths.Combine (Paths.LegacyApplicationData, "covers");

                if (!Directory.Exists (root_path)) {
                    Directory.Create (CoverArtSpec.RootPath);

                    if (Directory.Exists (legacy_artwork_path)) {
                        Directory.Move (new SafeUri (legacy_artwork_path), new SafeUri (root_path));
                    }
                }

                if (Directory.Exists (legacy_artwork_path)) {
                    Log.InformationFormat ("Deleting old (Banshee < 1.0) artwork cache directory {0}", legacy_artwork_path);
                    Directory.Delete (legacy_artwork_path, true);
                }
            }

            if (version < 2) {
                int deleted = 0;
                foreach (string dir in Directory.GetDirectories (root_path)) {
                    int size;
                    string dirname = System.IO.Path.GetFileName (dir);
                    if (Int32.TryParse (dirname, out size) && !IsCachedSize (size)) {
                        Directory.Delete (dir, true);
                        deleted++;
                    }
                }

                if (deleted > 0) {
                    Log.InformationFormat ("Deleted {0} extraneous album-art cache directories", deleted);
                }
            }

            CacheVersion = CUR_VERSION;
        }

        private static SafeUri cache_version_file = new SafeUri (Paths.Combine (CoverArtSpec.RootPath, ".cache_version"));
        private static int CacheVersion {
            get {
                if (Banshee.IO.File.Exists (cache_version_file)) {
                    using (var reader = new System.IO.StreamReader (Banshee.IO.File.OpenRead (cache_version_file))) {
                        int version;
                        if (Int32.TryParse (reader.ReadLine (), out version)) {
                            return version;
                        }
                    }
                }

                return 0;
            }
            set {
                using (var writer = new System.IO.StreamWriter (Banshee.IO.File.OpenWrite (cache_version_file, true))) {
                    writer.Write (value.ToString ());
                }
            }
        }

#endregion

    }
}
