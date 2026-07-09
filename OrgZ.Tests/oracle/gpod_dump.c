/* gpod_dump — the slice-C write-verification oracle.
 *
 * Parses an iPod library with libgpod's own itdb_parse() (the canonical reference
 * implementation) and prints every load-bearing track and playlist field as one JSON
 * object per line. OrgZ writes a database; this reads it back INDEPENDENTLY; the test
 * asserts libgpod saw exactly what OrgZ intended. Never uses OrgZ's own reader — that
 * would be the circularity slice B got burned by.
 *
 * Build (Debian/Ubuntu):  apt-get install -y libgpod-dev pkg-config gcc
 *   gcc gpod_dump.c -o gpod_dump $(pkg-config --cflags --libs libgpod-1.0)
 * Run:  ./gpod_dump /path/to/mountpoint      (dir containing iPod_Control/iTunes/iTunesDB)
 */
#include <gpod/itdb.h>
#include <stdio.h>
#include <glib.h>

static void jstr(const char *k, const char *v)
{
    printf("\"%s\":", k);
    if (!v) { printf("null"); return; }
    putchar('"');
    for (const unsigned char *p = (const unsigned char *)v; *p; p++)
    {
        switch (*p)
        {
            case '"':  fputs("\\\"", stdout); break;
            case '\\': fputs("\\\\", stdout); break;
            case '\n': fputs("\\n", stdout);  break;
            case '\r': fputs("\\r", stdout);  break;
            case '\t': fputs("\\t", stdout);  break;
            default:
                if (*p < 0x20) { printf("\\u%04x", *p); }
                else           { putchar(*p); }
        }
    }
    putchar('"');
}

int main(int argc, char **argv)
{
    if (argc < 2) { fprintf(stderr, "usage: gpod_dump <mountpoint>\n"); return 2; }

    GError *err = NULL;
    Itdb_iTunesDB *db = itdb_parse(argv[1], &err);
    if (!db)
    {
        fprintf(stderr, "itdb_parse failed: %s\n", err ? err->message : "(unknown)");
        if (err) g_error_free(err);
        return 1;
    }

    for (GList *t = db->tracks; t; t = t->next)
    {
        Itdb_Track *tr = (Itdb_Track *)t->data;
        printf("{\"kind\":\"track\",");
        printf("\"id\":%d,", tr->id);
        jstr("title", tr->title);       printf(",");
        jstr("artist", tr->artist);     printf(",");
        jstr("album", tr->album);       printf(",");
        jstr("genre", tr->genre);       printf(",");
        jstr("composer", tr->composer); printf(",");
        jstr("ipod_path", tr->ipod_path); printf(",");
        printf("\"size\":%lld,", (long long)tr->size);
        printf("\"tracklen\":%d,", tr->tracklen);
        printf("\"track_nr\":%d,\"tracks\":%d,", tr->track_nr, tr->tracks);
        printf("\"cd_nr\":%d,\"cds\":%d,", tr->cd_nr, tr->cds);
        printf("\"year\":%d,", tr->year);
        printf("\"bitrate\":%d,", tr->bitrate);
        printf("\"samplerate\":%d,", tr->samplerate);
        printf("\"rating\":%d,", tr->rating);
        printf("\"playcount\":%d,", tr->playcount);
        printf("\"skipcount\":%d,", tr->skipcount);
        printf("\"time_added\":%lld,", (long long)tr->time_added);
        printf("\"dbid\":%llu,", (unsigned long long)tr->dbid);
        printf("\"mediatype\":%u,", tr->mediatype);
        printf("\"skip_shuffle\":%u,", tr->skip_when_shuffling);
        printf("\"bookmark\":%u,", tr->remember_playback_position);
        printf("\"unplayed\":%u,", tr->mark_unplayed);
        printf("\"flag4\":%u,", tr->flag4);
        printf("\"time_released\":%lld,", (long long)tr->time_released);
        jstr("podcastrss", tr->podcastrss); printf(",");
        jstr("podcasturl", tr->podcasturl); printf(",");
        jstr("description", tr->description);
        printf("}\n");
    }

    for (GList *p = db->playlists; p; p = p->next)
    {
        Itdb_Playlist *pl = (Itdb_Playlist *)p->data;
        printf("{\"kind\":\"playlist\",");
        jstr("name", pl->name); printf(",");
        printf("\"is_master\":%s,", itdb_playlist_is_mpl(pl) ? "true" : "false");
        printf("\"is_podcast\":%s,", itdb_playlist_is_podcasts(pl) ? "true" : "false");
        printf("\"members\":[");
        for (GList *m = pl->members; m; m = m->next)
        {
            Itdb_Track *tr = (Itdb_Track *)m->data;
            printf("%s%d", (m == pl->members) ? "" : ",", tr->id);
        }
        printf("]}\n");
    }

    itdb_free(db);
    return 0;
}
