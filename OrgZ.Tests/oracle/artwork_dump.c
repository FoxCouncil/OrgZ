/* artwork_dump — the slice-F artwork oracle.
 *
 * Reads an iPod mountpoint with libgpod's itdb_parse (which also parses the ArtworkDB) and reports,
 * per track, the artwork libgpod links to it — the linkage dbid, whether the thumbnail decodes, its
 * decoded dimensions and a sampled pixel — so OrgZ's ArtworkDB + .ithmb are verified by the reference
 * implementation, never by OrgZ's own reader. libgpod links artwork to a track by artwork->dbid ==
 * track->dbid (mhii->song_id), and prints "Could not find corresponding track" to stderr when that
 * link fails, so a linkage bug is loud.
 *
 * Build: gcc artwork_dump.c -o artwork_dump $(pkg-config --cflags --libs libgpod-1.0 gdk-pixbuf-2.0)
 * Run:   ./artwork_dump <mountpoint> [modelnumstr]      (e.g. xMA446 for an iPod Video 5.5G)
 */
#include <gpod/itdb.h>
#include <gdk-pixbuf/gdk-pixbuf.h>
#include <stdio.h>

int main(int argc, char **argv)
{
    if (argc < 2) { fprintf(stderr, "usage: artwork_dump <mountpoint> [modelnumstr]\n"); return 2; }

    GError *err = NULL;
    Itdb_iTunesDB *db = itdb_parse(argv[1], &err);
    if (!db) { fprintf(stderr, "itdb_parse failed: %s\n", err ? err->message : "(unknown)"); return 1; }

    /* Give libgpod the model so it knows the artwork format (dimensions / RGB565 endianness). */
    if (argc >= 3 && db->device) { itdb_device_set_sysinfo(db->device, "ModelNumStr", argv[2]); }

    for (GList *t = db->tracks; t; t = t->next)
    {
        Itdb_Track *tr = (Itdb_Track *)t->data;
        Itdb_Artwork *aw = tr->artwork;
        printf("{\"id\":%u,\"dbid\":%llu,\"has_artwork\":%s",
               tr->id, (unsigned long long)tr->dbid, aw ? "true" : "false");
        printf(",\"mhii_link\":%u", tr->mhii_link);
        if (aw)
        {
            printf(",\"art_dbid\":%llu,\"art_id\":%u,\"art_size\":%u",
                   (unsigned long long)aw->dbid, aw->id, aw->artwork_size);
            GdkPixbuf *pb = (GdkPixbuf *)itdb_artwork_get_pixbuf(db->device, aw, -1, -1);
            if (pb && GDK_IS_PIXBUF(pb))
            {
                int w = gdk_pixbuf_get_width(pb);
                int h = gdk_pixbuf_get_height(pb);
                guchar *px = gdk_pixbuf_get_pixels(pb);
                printf(",\"w\":%d,\"h\":%d,\"px0\":[%d,%d,%d]", w, h, px[0], px[1], px[2]);
                g_object_unref(pb);
            }
            else
            {
                printf(",\"pixbuf\":null");
            }
        }
        printf("}\n");
    }

    itdb_free(db);
    return 0;
}
