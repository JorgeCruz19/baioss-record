# Multichannel audio manual — Baioss Record

*How to record SDI or NDI signals that carry 8 or 16 audio channels: choosing which pairs are recorded, measuring what
arrives, deciding how the tracks are stored and reading the meters. It is written for the operator; the technical
details are in `FFMPEG.md`.*

> *(This is the English version of `MANUAL-AUDIO-MULTICANAL.md`; the two are kept in step.)*

---

## 1. The basics in one minute

An SDI signal carries its audio **embedded** in **pairs** of channels: pair 1 is channels 1-2, pair 2 is channels 3-4,
and so on up to 16 channels (8 pairs). In broadcast each pair usually carries something different: the **programme** on
1-2, the **international sound** (no commentary) on 3-4, **languages** or **commentary** on the following ones.

Until now the program always recorded pair 1-2 and discarded the rest. Now **you decide**:

| Decision | Where it is made | What it means |
|---|---|---|
| **How many channels to request from the card** | 🎛 Inputs → *Card audio* | 2, 8, 16 or **Automatic** (as many as the card allows) |
| **Which pair (or pairs) are recorded** | 🎛 Inputs → *Pair to record* | One specific pair, or **All pairs** |
| **How they are stored in the file** | ⚙ Recording presets → *Audio tracks* | One track, one track per pair, or every channel in one track |

Two things worth being clear about from the start:

- **The card does not know what the signal carries.** You ask it for a number of channels and it delivers that number;
  the ones that do not exist arrive as silence. That is why a quiet pair does not mean an absent pair, and why the
  choice is yours (with the help of the **🎧 Measure audio** button).
- **The choice stays fixed while recording.** The tracks of a file cannot change halfway through. If you change the
  pair, the change is applied when the input reconnects (the **Apply** button), never on a recording in progress.

---

## 2. Requirements

- A **DeckLink** card with audio embedded in the SDI. The card accepts requests for **2, 8 or 16** channels: if your
  signal carries 4 or 6, request 8 (or leave *Automatic*) and the channels that do not exist stay silent.
- With **NDI** the channels are set by the source (for example, an OBS with 2 channels or a mixer with 8): nothing is
  requested, you only choose the pair to record.
- To **measure** the audio, the card has to be **free**: no channel may be capturing it at that moment (a DeckLink can
  only be opened by one process at a time).

---

## 3. Step by step: configuring the input (🎛 Inputs)

1. Press **🔍 Detect devices** and, on the channel's row, choose the **DeckLink** (or the NDI source) as the video input.
2. Under **Card audio (DeckLink)** choose how many channels to request:
   - **Automatic (as many as the card allows)** — recommended. It asks for 16 and, if the card cannot do it, falls back
     to 8 and then 2 by itself. The channel panel shows how many it ended up with ("Pair 1-2 of 8 · PCM").
   - **2 / 8 / 16 channels** — if you know what your installation carries and want to fix it.
3. If you do not know what each pair carries, press **🎧 Measure audio** (with the card free). The program listens for
   about three seconds and shows you the peak level of each pair under the drop-down, and a summary in the bottom bar:

   > 1-2: −8 dB · 3-4: −20 dB · 5-6: silence · 7-8: silence
   >
   > Channel A: the card delivers 8 channels; with sound: pairs 1-2, 3-4. To record: Pair 1-2 (press Apply to save it).

   It also **proposes the first pair with sound** and, if you were on *Automatic*, fixes the count the card accepted.
   A pair counts as silent below −60 dBFS. The proposal is **not saved** until you press **Apply**. If you had already
   chosen *All pairs*, the measurement leaves that choice alone.
4. Under **Pair to record** choose:
   - **Pair 1-2, Pair 3-4, …** — only that pair is recorded (stereo). This is the usual choice when you want the
     programme or one specific language.
   - **All pairs** — every channel the card delivers is recorded. This is what you need to keep the programme and the
     international sound in separate tracks, or to preserve all the SDI audio (see section 4).
5. Press **Apply**. The channel reconnects to the input with the new configuration and the message at the bottom
   confirms it ("Channel A → DeckLink … · audio: Pair 3-4 (Automatic…)"). In the channel panel, under the preview, you
   will see the choice: "**Pair 3-4 of 8 · PCM**" or "**All 8 channels · PCM**".

> **Note:** when you open the Inputs window, the drop-downs show the default values (*Automatic* and *Pair 1-2*), not
> what the channel has right now. If you apply an input again, choose the audio and the pair again.

---

## 4. Step by step: deciding the tracks of the file (⚙ Recording presets)

Open the preset the channel uses (**✎ Edit**, or **＋ New** starting from an existing one) and look at the
**Audio tracks** field. It only matters when the input delivers more than 2 channels:

| Mode | What it produces | When to use it | Formats |
|---|---|---|---|
| **Single** | A single track with the selection. With one pair → stereo. With several pairs and a **stereo** preset (what every factory preset carries) → **one stereo track per pair**, just like *PairsAsTracks*: a stereo track cannot carry more than one pair, and what was chosen in the input is not thrown away. With *All pairs* and a **5.1 / 7.1** preset → the first 6 or 8 channels form that track. | The usual: one stereo. Or a 5.1 embedded in channels 1-6. | All |
| **PairsAsTracks** | **One stereo track per recorded pair**, each with its name ("Channels 1-2", "Channels 3-4"…). Nothing is mixed. | Programme + international + languages, each in its own track, ready for the editor. Needs *All pairs*. | All. With AAC (MP4/MOV/TS) each track adds bitrate; with PCM (MXF/MKV) nothing is lost. |
| **Multichannel** | **Every channel in a single** uncompressed multichannel **track**. | Preserving the whole SDI for post-production. Needs *All pairs*. | **PCM only: MXF, MKV, AVI or WAV**, up to 16 channels. With a lossy codec (AAC in MP4/MOV/TS, Opus, MP2, MP3) the program stores **one stereo track per pair**, just like *PairsAsTracks* (see the note below). |

> **Why *Multichannel* does not make a 7.1 track in MP4.** Lossy codecs treat **channel 4** of a 5.1/7.1 track as the
> bass channel (LFE) and strip everything that is not bass: if that channel carries, say, the right side of the
> international sound, it would be ruined (we measured it: −89 dB). MP2 and MP3, in addition, only support stereo and
> would mix all eight channels without warning. That is why, with those codecs, the program always stores one stereo
> track per pair: nothing is mixed and nothing is cut.

Practical recommendations:

- To **keep everything lossless**, use a preset with an **MXF** or **MKV** container (PCM audio). In MP4/MOV/TS the
  program converts the audio to AAC.
- An **audio-only** preset to **WAV** only holds one track: with *PairsAsTracks* the program stores every chosen
  channel in one multichannel WAV track. To **MP3** (one track, stereo only) goes the first chosen pair.
- If your SDI carries a **real 5.1** on channels 1-6, use *Single* with a 5.1 preset (there channel 4 really is the
  LFE). If what it carries are independent pairs, do **not** use a 5.1/7.1 preset with AAC: use *PairsAsTracks*.
- If you choose **All pairs** with a 16-channel input and *PairsAsTracks*, the file will have 8 stereo tracks,
  including those of the empty pairs (silent). With PCM it does not matter; with AAC it takes a little more space.
- The **track names** ("Channels 3-4", or "Canales 3-4" with the application in Spanish) show up in Premiere, Resolve,
  VLC or MediaInfo, so the editor knows what each one is.
- The **test pattern** (when the signal is lost) generates silence with exactly the same tracks, so the file does not
  change structure halfway through.

Then apply the preset to the channel (**Apply to channel**) as with any other.

---

## 5. Reading the meters

In the **AUDIO dBFS** strip of the channel panel:

- The **large L / R meters** show the **first recorded pair**, with its value in dBFS and the red **CLIP** warning if
  it clips.
- With an 8- or 16-channel input a **mini meter per pair** ("1-2", "3-4", …) appears to their right with **everything**
  the card delivers:
  - **Red dot + bold label** = that pair is recorded.
  - Grey label = that pair is **only metered** (it does not go into the file). It lets you see at a glance whether
    sound is arriving on a pair you are not recording, for example if you picked the wrong pair.
  - Hovering over it says so in words ("Channels 3-4: not recorded (metered only)").
  - The header of the strip says how many **tracks** will go into the file with the current input and preset ("All 16
    channels · PCM · 8 tracks", "Pair 3-4 of 8 · PCM · 1 stereo track", "1 5.1 track", "1 track · 16 channels"). If it
    is not what you expected, check *Audio tracks* in the preset: no need to record and open the file to find out.
- The **silence alarm** watches the recorded pair, not the rest: an empty pair you are not recording triggers nothing.

In the **web client** the *Audio · 8 source channels · 4 tracks* block shows the same grid per pair, with the red mark
on the recorded ones, the tracks that will go into the file and the legend "● recorded · the rest is only metered".

---

## 6. Typical setups

**Just the programme (pair 1-2).** Nothing to touch: *Automatic* + *Pair 1-2* + your usual preset.

**The programme arrives on pair 3-4.** Inputs → 🎧 Measure audio → the program proposes *Pair 3-4* → Apply. In the
panel: "Pair 3-4 of 8 · PCM".

**Programme and international sound in separate tracks.** Inputs → *All pairs* → Apply. Preset → *Audio tracks* =
**PairsAsTracks** (ideally MXF/MKV with PCM). The file carries "Channels 1-2", "Channels 3-4", … With a stereo preset
left on *Single* (the factory value) the result is the same: one track per pair.

**Preserving all the SDI audio for post-production.** Inputs → *All pairs*. MXF or MKV preset with *Audio tracks* =
**Multichannel**: one PCM track with the 8 or 16 channels.

**A 5.1 embedded in channels 1-6.** Inputs → *All pairs*. Preset with *Channels* = **5.1** and *Audio tracks* =
**Single**: channels 1-6 form the 5.1 track (L, R, C, LFE, Ls, Rs, in that order).

**NDI source with 4 channels.** Inputs → NDI source → *Pair to record* (*Pair 1-2*, *Pair 3-4* or *All pairs*) →
Apply. There is no *Card audio*: the channels come from the source.

---

## 7. What happens if…

- **The card does not support 16 channels.** With *Automatic* the program falls back to 8 and then 2 by itself and
  notes it in the program log (the `logs` folder). If you had fixed 16 by hand, the card does not open at all —the
  channel has neither picture nor sound— and the log explains why: go back to Inputs and choose 8 or 2.
- **"Measure audio" finds no sound.** Check that no channel is using the card (it has to be free), that the signal
  carries embedded audio and that the level is above −60 dBFS. If the card is busy, the message at the bottom says so.
- **You chose pair 5-6 but the card only delivers 2 channels.** Pair 1-2 is recorded (better to record the pair that
  exists than to record nothing). The panel shows "2 channels · PCM".
- **You changed the pair and the recording stays the same.** That is expected: the recording in progress does not
  change. The new pair is applied when the input reconnects (Apply) and takes effect in the next recording.
- **You chose *Multichannel* and the MP4 has several stereo tracks instead of one multichannel track.** That is
  expected with AAC (see the note in section 4). For a single track with the 8 or 16 channels use MXF or MKV (PCM).
- **You chose *All pairs* and the file has a single stereo track.** Until 2026-09-22 this happened with the preset on
  *Single* (the factory value): only pair 1-2 was saved even though the meters marked every pair as recorded. Now
  *Single* with stereo and several pairs saves one track per pair, and the header of the audio strip says how many
  tracks will go into the file. If you see "1 track" with several pairs chosen, the preset carries mono, 5.1/7.1 or
  MP3, which are a single track on purpose.
- **"Measure audio" does not open the card even though it is free.** If your signal needs a specific *Mode / format*,
  choose it before measuring: the measurement opens the card with that same mode.

---

## 8. For support: what is underneath

The choice is stored in the input's parameters (`InputSources` table, `Parameters` column, JSON):

- `audio_channels` = `2` | `8` | `16` | `auto` (DeckLink only).
- `audio_pairs` = `1` | `2` | … | `all`. From the Inputs window you choose one pair or all of them; a specific
  combination ("1,3" = channels 1-2 and 5-6) can only be written here, by hand.

Without these parameters the channel behaves as always: 2 channels, pair 1-2. The detail of the FFmpeg graph (selection
with `pan`, meters with `ebur128`, track modes, test pattern) is in `docs/FFMPEG.md`, section "Audio embebido
multicanal" (in Spanish).
