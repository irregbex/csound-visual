/*
 * link_peer_test: a second Ableton Link peer used by Csound Visual's tests.
 *
 * It joins the Link session through CsoundVisualLink.dll (the same C API the app uses) and, on a
 * fixed grid of the Link clock (abl_link_clock_micros, i.e. QueryPerformanceCounter, which every
 * process on the machine shares), prints one line per grid point:
 *
 *     G <grid time µs> <peers> <tempo> <beat at grid time> <phase at grid time> <playing>
 *
 * Another process that evaluates its own timeline at the same grid times can then compare the
 * beats directly: two synchronised peers must report the same beat for the same host time.
 *
 * Usage:
 *   link_peer_test [--bpm 97] [--seconds 10] [--quantum 4] [--grid-ms 100]
 *                  [--tempo-at SEC:BPM]... [--play-at SEC] [--stop-at SEC] [--start-stop]
 *
 * --tempo-at   proposes a new session tempo SEC seconds after start (may be repeated; only the
 *              first 8 are used, later ones are ignored with a warning on stderr).
 * --play-at / --stop-at   start/stop the shared transport (needs --start-stop to be shared).
 * Nothing is played: the program makes no sound.
 *
 * Defaults: --bpm 97 (initial tempo; an existing session's tempo wins when joining),
 * --seconds 10 (run time), --quantum 4 (beats per bar, used for beat/phase), --grid-ms 100
 * (minimum 5).
 *
 * Output (stdout, one event per line, flushed immediately so a parent process can read it
 * live; times are Link clock microseconds):
 *     READY <t0> <bpm>             joined (Link enabled), t0 = start time
 *     TEMPO <time> <bpm>           this peer proposed a tempo change (--tempo-at)
 *     PLAY <time> / STOP <time>    this peer started/stopped the transport
 *     G ...                        one grid line (format above); <peers> = number of OTHER peers
 *     END                          left the session and exited
 * Exit code 0, or 2 for an unknown/incomplete argument.
 *
 * Who runs it: tools/build_link.ps1 (two-peer smoke test: two instances at different tempos,
 * one proposing 133 BPM after 2 s; both must end up seeing each other and at 133 BPM) and the
 * app's Link self-test (src/CsoundVisual/Integration/LinkSelfTest.cs). Built next to
 * CsoundVisualLink.dll in src/AbletonLink/build/bin/<config>/; run it from there, e.g.
 *     link_peer_test.exe --bpm 120 --seconds 5 --tempo-at 2:133
 *
 * Threading: everything runs on the main thread, which is an "application" thread in Link's
 * terms, so it uses the *_app_session_state functions (the *_audio_* ones are reserved for a
 * real-time audio callback, which this program does not have).
 */
#include <abl_link.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <windows.h>

/* timeBeginPeriod/timeEndPeriod live in winmm. */
#pragma comment(lib, "winmm.lib")

/* One scheduled tempo proposal from --tempo-at SEC:BPM: `at` seconds after start, propose
 * `bpm`; `done` is set once it has been committed. */
typedef struct { double at; double bpm; int done; } TempoChange;

/* Parses the arguments, joins the Link session, then polls about every millisecond until
 * --seconds have elapsed: applies the scheduled tempo/transport changes and prints one G line
 * per grid point. Returns 0, or 2 on a bad argument. */
int main(int argc, char **argv)
{
    double bpm = 97, seconds = 10, quantum = 4, playAt = -1, stopAt = -1;
    int gridMs = 100, startStop = 0, nChanges = 0;
    TempoChange changes[8];
    memset(changes, 0, sizeof changes);
    /* Minimal argument parser: options taking a value consume the next argv entry (`v`) and
     * advance i. A --tempo-at value without ':' is skipped silently, and so is a 9th (or
     * later) --tempo-at: its value is consumed and a warning printed on stderr. (Before, the
     * `nChanges < 8` test sat in the option match, so a 9th --tempo-at fell through to the
     * "unknown argument" branch and the program exited with code 2, contradicting the usage
     * text "max 8".) */
    for (int i = 1; i < argc; i++)
    {
        const char *a = argv[i];
        const char *v = i + 1 < argc ? argv[i + 1] : NULL;
        if (!strcmp(a, "--bpm") && v) { bpm = atof(v); i++; }
        else if (!strcmp(a, "--seconds") && v) { seconds = atof(v); i++; }
        else if (!strcmp(a, "--quantum") && v) { quantum = atof(v); i++; }
        else if (!strcmp(a, "--grid-ms") && v) { gridMs = atoi(v); i++; }
        else if (!strcmp(a, "--play-at") && v) { playAt = atof(v); i++; }
        else if (!strcmp(a, "--stop-at") && v) { stopAt = atof(v); i++; }
        else if (!strcmp(a, "--start-stop")) startStop = 1;
        else if (!strcmp(a, "--tempo-at") && v)
        {
            const char *colon = strchr(v, ':');
            if (nChanges >= 8)
                fprintf(stderr, "ignoring --tempo-at %s (at most 8)\n", v);
            else if (colon)
            {
                changes[nChanges].at = atof(v);
                changes[nChanges].bpm = atof(colon + 1);
                nChanges++;
            }
            i++;
        }
        else
        {
            fprintf(stderr, "unknown argument %s\n", a);
            return 2;
        }
    }
    if (gridMs < 5) gridMs = 5;

    /* 1 ms system timer resolution, so Sleep(1) below really sleeps ~1 ms (the default tick
     * is ~15.6 ms, far too coarse for a 100 ms grid sampled on time). Undone at the end. */
    timeBeginPeriod(1);
    /* Create the instance (disabled), one reusable session-state object for all captures,
     * choose whether transport start/stop is shared, then join the session. */
    abl_link link = abl_link_create(bpm);
    abl_link_session_state state = abl_link_create_session_state();
    abl_link_enable_start_stop_sync(link, startStop != 0);
    abl_link_enable(link, true);

    const int64_t gridUs = (int64_t)gridMs * 1000;
    const int64_t t0 = abl_link_clock_micros(link);
    printf("READY %lld %.3f\n", (long long)t0, bpm);
    fflush(stdout);

    /* Grid points are absolute multiples of gridUs on the shared clock (not offsets from
     * t0), so two processes started at different moments sample the timeline at exactly the
     * same host times and their G lines can be matched by the first column. */
    int64_t next = (t0 / gridUs + 1) * gridUs;
    int played = 0, stopped = 0;
    for (;;)
    {
        int64_t now = abl_link_clock_micros(link);
        double elapsed = (now - t0) / 1e6;
        if (elapsed >= seconds) break;

        /* Scheduled tempo proposals: capture the session, change its tempo effective `now`,
         * commit. Link then propagates the new tempo to every peer. */
        for (int c = 0; c < nChanges; c++)
        {
            if (!changes[c].done && elapsed >= changes[c].at)
            {
                changes[c].done = 1;
                abl_link_capture_app_session_state(link, state);
                abl_link_set_tempo(state, changes[c].bpm, now);
                abl_link_commit_app_session_state(link, state);
                printf("TEMPO %lld %.3f\n", (long long)now, changes[c].bpm);
                fflush(stdout);
            }
        }
        /* Transport start: play from `now` and ask that beat 0 fall on `now`. When other peers
         * are connected, Link moves beat 0 to the next moment whose phase (within `quantum`)
         * matches the session's, so the bars stay aligned. The start is shared with the peers
         * only with --start-stop. */
        if (playAt >= 0 && !played && elapsed >= playAt)
        {
            played = 1;
            abl_link_capture_app_session_state(link, state);
            abl_link_set_is_playing_and_request_beat_at_time(state, true, (uint64_t)now, 0, quantum);
            abl_link_commit_app_session_state(link, state);
            printf("PLAY %lld\n", (long long)now);
            fflush(stdout);
        }
        if (stopAt >= 0 && !stopped && elapsed >= stopAt)
        {
            stopped = 1;
            abl_link_capture_app_session_state(link, state);
            abl_link_set_is_playing(state, false, (uint64_t)now);
            abl_link_commit_app_session_state(link, state);
            printf("STOP %lld\n", (long long)now);
            fflush(stdout);
        }

        /* A grid point has passed: evaluate the timeline AT the grid time (not at `now`, which
         * is up to ~1 ms later), so the values are comparable across processes. If the loop
         * was late by more than one grid step, the missed points are skipped (`next` jumps to
         * the first grid point after `now`). */
        if (now >= next)
        {
            abl_link_capture_app_session_state(link, state);
            printf("G %lld %llu %.4f %.6f %.6f %d\n",
                (long long)next,
                (unsigned long long)abl_link_num_peers(link),
                abl_link_tempo(state),
                abl_link_beat_at_time(state, next, quantum),
                abl_link_phase_at_time(state, next, quantum),
                abl_link_is_playing(state) ? 1 : 0);
            fflush(stdout);
            next = (now / gridUs + 1) * gridUs;
        }
        Sleep(1);
    }

    /* Leave the session, then free in reverse order of creation. */
    abl_link_enable(link, false);
    abl_link_destroy_session_state(state);
    abl_link_destroy(link);
    timeEndPeriod(1);
    printf("END\n");
    return 0;
}
