//
// DatabaseTrackInfo.cs
//
// Author:
//   Aaron Bockover <abockover@novell.com>
//
// Copyright (C) 2007 Novell, Inc.
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
using System.Data;
using System.IO;

using Hyena;
using Hyena.Data.Sqlite;

using Banshee.Base;
using Banshee.Configuration.Schema;
using Banshee.Database;
using Banshee.Sources;
using Banshee.IO;
using Banshee.ServiceStack;

// Disabling "is never used" warnings here because there are a lot
// of properties/fields that are set via reflection at the database
// layer - that is, they really are used, but the compiler doesn't
// think so.

#pragma warning disable 0169

namespace Banshee.Collection.Database
{
    public enum TrackUriType : int {
        AbsolutePath = 0,
        RelativePath = 1,
        AbsoluteUri = 2
    }

    public class DatabaseTrackInfo : TrackInfo, ICacheableItem
    {
        private static BansheeModelProvider<DatabaseTrackInfo> provider = new BansheeModelProvider<DatabaseTrackInfo> (
            ServiceManager.DbConnection, "CoreTracks"
        );

        public static BansheeModelProvider<DatabaseTrackInfo> Provider {
            get { return provider; }
        }

        private bool? artist_changed = null, album_changed = null;
        private bool uri_fields_dirty = false;
        
        public DatabaseTrackInfo () : base ()
        {
        }

        public override void IncrementPlayCount ()
        {
            if (Provider.Refresh (this)) {
                base.IncrementPlayCount ();
                Save ();
            }
        }

        public override void IncrementSkipCount ()
        {
            if (Provider.Refresh (this)) {
                base.IncrementSkipCount ();
                Save ();
            }
        }

        public override bool TrackEqual (TrackInfo track)
        {
            if (!(track is DatabaseTrackInfo)) {
                return base.TrackEqual (track);
            }
            
            DatabaseTrackInfo db_track = (DatabaseTrackInfo)track;
            return db_track.TrackId == TrackId && db_track.CacheModelId == CacheModelId && 
                db_track.CacheEntryId == CacheEntryId;
        }
        
        public override void Save ()
        {
            Save (true);
        }
        
        public DatabaseArtistInfo Artist {
            get { return DatabaseArtistInfo.FindOrCreate (ArtistName); }
        }
        
        public DatabaseAlbumInfo Album {
            get { return DatabaseAlbumInfo.FindOrCreate (Artist, AlbumTitle); }
        }

        public void Save (bool notify)
        {
            // If either the artist or album changed, 
            if (ArtistId == 0 || AlbumId == 0 || artist_changed == true || album_changed == true) {
                DatabaseArtistInfo artist = Artist;
                ArtistId = artist.DbId;
           
                DatabaseAlbumInfo album = Album;
                AlbumId = album.DbId;
                
                // TODO get rid of unused artists/albums
            }
            
            DateUpdated = DateTime.Now;
            bool is_new = TrackId == 0;
            Provider.Save (this);

            if (notify) {
                if (is_new) {
                    PrimarySource.NotifyTracksAdded ();
                } else {
                    PrimarySource.NotifyTracksChanged ();
                }
            }
        }
        
        [DatabaseColumn ("TrackID", Constraints = DatabaseColumnConstraints.PrimaryKey)]
        private int track_id;
        public int TrackId {
            get { return track_id; }
        }

        private int primary_source_id;
        [DatabaseColumn ("PrimarySourceID")]
        public int PrimarySourceId {
            get { return primary_source_id; }
            set {
                primary_source_id = value;
                UpdateUri ();
            }
        }

        public PrimarySource PrimarySource {
            get { return PrimarySource.GetById (primary_source_id); }
            set { primary_source_id = value.DbId; }
        }

        [DatabaseColumn ("ArtistID")]
        private int artist_id;
        public int ArtistId {
            get { return artist_id; }
            set { artist_id = value; }
        }

        [DatabaseColumn ("AlbumID")]
        private int album_id;
        public int AlbumId {
            get { return album_id; }
            set { album_id = value; }
        }

        [VirtualDatabaseColumn ("Name", "CoreArtists", "ArtistID", "ArtistID")]
        public override string ArtistName {
            get { return base.ArtistName; }
            set {
                if (value == ArtistName)
                    return;

                base.ArtistName = value;
                artist_changed = artist_changed != null;
            }
        }
        
        [VirtualDatabaseColumn ("Title", "CoreAlbums", "AlbumID", "AlbumID")]
        public override string AlbumTitle {
            get { return base.AlbumTitle; }
            set {
                if (value == AlbumTitle)
                    return;

                base.AlbumTitle = value;
                album_changed = album_changed != null;
            }
        }
        
        private int tag_set_id;
        [DatabaseColumn]
        public int TagSetID {
            get { return tag_set_id; }
            set { tag_set_id = value; }
        }
        
        [DatabaseColumn ("MusicBrainzID")]
        public override string MusicBrainzId {
            get { return base.MusicBrainzId; }
            set { base.MusicBrainzId = value; }
        }

        public override SafeUri Uri {
            get { return base.Uri; }
            set {
                base.Uri = value;
                uri_fields_dirty = true;
            }
        }
        
        private string uri_field;
        [DatabaseColumn ("Uri")]
        private string UriField {
            get {
                if (uri_fields_dirty) {
                    PrimarySource.UriToFields (Uri, out uri_type, out uri_field);
                    uri_fields_dirty = false;
                }
                return uri_field;
            }
            set {
                uri_field = value;
                UpdateUri ();
            }
        }
        
        private bool uri_type_set;
        private TrackUriType uri_type;
        [DatabaseColumn ("UriType")]
        private TrackUriType UriType {
            get {
                if (uri_fields_dirty) {
                    PrimarySource.UriToFields (Uri, out uri_type, out uri_field);
                    uri_fields_dirty = false;
                }
                return uri_type;
            }
            set {
                uri_type = value;
                uri_type_set = true;
                UpdateUri ();
            }
        }
        
        [DatabaseColumn]
        public override string MimeType {
            get { return base.MimeType; }
            set { base.MimeType = value; }
        }
        
        [DatabaseColumn]
        public override long FileSize {
            get { return base.FileSize; }
            set { base.FileSize = value; }
        }
        
        [DatabaseColumn ("Attributes")]
        public override TrackMediaAttributes MediaAttributes {
            get { return base.MediaAttributes; }
            set { base.MediaAttributes = value; }
        }
        
        [DatabaseColumn ("Title")]
        public override string TrackTitle {
            get { return base.TrackTitle; }
            set { base.TrackTitle = value; }
        }
        
        [DatabaseColumn(Select = false)]
        protected string TitleLowered {
            get { return TrackTitle == null ? null : TrackTitle.ToLower (); }
        }
        
        [DatabaseColumn]
        public override int TrackNumber {
            get { return base.TrackNumber; }
            set { base.TrackNumber = value; }
        }
        
        [DatabaseColumn]
        public override int TrackCount {
            get { return base.TrackCount; }
            set { base.TrackCount = value; }
        }
        
        [DatabaseColumn]
        public override int Disc {
            get { return base.Disc; }
            set { base.Disc = value; }
        }
        
        [DatabaseColumn]
        public override TimeSpan Duration {
            get { return base.Duration; }
            set { base.Duration = value; }
        }
        
        [DatabaseColumn]
        public override int Year {
            get { return base.Year; }
            set { base.Year = value; }
        }

        [DatabaseColumn]
        public override string Genre {
            get { return base.Genre; }
            set { base.Genre = value; }
        }

        [DatabaseColumn]
        public override string Composer {
            get { return base.Composer; }
            set { base.Composer = value; }
        }

        [DatabaseColumn]
        public override string Copyright {
            get { return base.Copyright; }
            set { base.Copyright = value; }
        }

        [DatabaseColumn]
        public override string LicenseUri {
            get { return base.LicenseUri; }
            set { base.LicenseUri = value; }
        }

        [DatabaseColumn]
        public override string Comment {
            get { return base.Comment; }
            set { base.Comment = value; }
        }
        
        [DatabaseColumn]
        public override int Rating {
            get { return base.Rating; }
            set { base.Rating = value; }
        }
        
        [DatabaseColumn]
        public override int PlayCount {
            get { return base.PlayCount; }
            set { base.PlayCount = value; }
        }
        
        [DatabaseColumn]
        public override int SkipCount {
            get { return base.SkipCount; }
            set { base.SkipCount = value; }
        }
        
        [DatabaseColumn ("LastPlayedStamp")]
        public override DateTime LastPlayed {
            get { return base.LastPlayed; }
            set { base.LastPlayed = value; }
        }

        [DatabaseColumn ("LastSkippedStamp")]
        public override DateTime LastSkipped {
            get { return base.LastSkipped; }
            set { base.LastSkipped = value; }
        }
        
        [DatabaseColumn ("DateAddedStamp")]
        public override DateTime DateAdded {
            get { return base.DateAdded; }
            set { base.DateAdded = value; }
        }

        private DateTime date_updated;
        [DatabaseColumn ("DateUpdatedStamp")]
        public DateTime DateUpdated {
            get { return date_updated; }
            set { date_updated = value; }
        }

        private long cache_entry_id;
        public long CacheEntryId {
            get { return cache_entry_id; }
            set { cache_entry_id = value; }
        }

        private long cache_model_id;
        public long CacheModelId {
            get { return cache_model_id; }
            set { cache_model_id = value; }
        }

        private void UpdateUri ()
        {
            if (Uri == null && uri_type_set && UriField != null && PrimarySource != null) {
                Uri = PrimarySource.UriAndTypeToSafeUri (UriType, UriField);
            }
        }
        
        public void CopyToLibraryIfAppropriate (bool force_copy)
        {
            SafeUri old_uri = this.Uri;
            if (old_uri == null) {
                // Get out quick, no URI set yet.
                return;
            }
            
            bool in_library = old_uri.AbsolutePath.StartsWith (Paths.CachedLibraryLocation);
            //Console.WriteLine ("{0} is{1}in library.", old_uri.ToString (), in_library ? " " : " not ");

            if (!in_library && (LibrarySchema.CopyOnImport.Get () || force_copy)) {
                string new_filename = FileNamePattern.BuildFull (this, Path.GetExtension (old_uri.ToString ()).Substring (1));
                SafeUri new_uri = new SafeUri (new_filename);

                try {
                    if (Banshee.IO.File.Exists (new_uri)) {
                        return;
                    }
                    
                    Banshee.IO.File.Copy (old_uri, new_uri, false);
                    Uri = new_uri;
                } catch (Exception e) {
                    Log.Error (String.Format("Exception copying into library: {0}", e), false);
                }
            }
        }

        private static HyenaSqliteCommand check_command = new HyenaSqliteCommand (
            "SELECT COUNT(*) FROM CoreTracks WHERE PrimarySourceId IN (?) AND (Uri = ? OR Uri = ?)"
        );
        
        public static bool ContainsUri (SafeUri uri, string relative_path, int [] primary_sources)
        {
            return ServiceManager.DbConnection.Query<int> (check_command.ApplyValues (primary_sources, relative_path, uri.AbsoluteUri)) > 0;
        }
    }
}

#pragma warning restore 0169

