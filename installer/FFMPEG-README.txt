BAIOSS RECORD — FFMPEG IS STILL MISSING (required step, done only once)
================================================================================

Baioss Record uses FFmpeg as its capture and recording engine. Because of the licensing
of that component we cannot ship it inside the installer: you have to download it and
leave it in THIS folder. It takes a moment and nothing has to be installed.

WHILE IT IS MISSING, the program opens and you can configure it, but it DOES NOT RECORD
(it starts in demonstration mode and warns you when it opens).

(Hay una versión en español de este archivo en FFMPEG-LEEME.txt, en esta misma carpeta.)


WHAT YOU HAVE TO DO
--------------------------------------------------------------------------------

1. Download an FFmpeg build for 64-bit Windows.

   The one we test Baioss Record with is "ffmpeg-autobuild" (AnimMouse's project on
   GitHub), which includes DeckLink support:

       https://github.com/AnimMouse/ffmpeg-autobuild/releases

   From the latest release, download the file whose name ends in "-win64-nonfree.7z"
   (about 105 MB). Read "IF YOU RECORD WITH AN NVIDIA CARD" below first.

   The official FFmpeg page (https://ffmpeg.org/download.html) links to other builds, but
   the best-known ones do NOT include "decklink" support. If you capture with Blackmagic
   DeckLink cards, check it with step 4.

   IF YOU RECORD WITH AN NVIDIA CARD ("NVENC" presets), check which card you have:

   · GeForce GTX 16xx, RTX 20xx, 30xx, 40xx or 50xx, Quadro RTX, Quadro T400/T600/T1000,
     RTX A-series or newer: the latest release works. It needs NVIDIA driver 610 or
     later; update your driver if it is older.

   · GeForce GTX 750, 9xx or 10xx, Quadro M... or P... (for example Quadro P400, P620 or
     P2000), Titan X, Xp or V: NVIDIA does not offer driver 610 for them (their last
     driver is the 580 series). With the latest release the program still records, but
     with the Intel graphics or the processor, and it warns you. Download FFmpeg 8.0
     instead, the stable build that the same author publishes in "ffmpeg-stable-autobuild".
     Get EXACTLY this release, which works with driver 570 or later:

       https://github.com/AnimMouse/ffmpeg-stable-autobuild/releases/tag/2025-09-03-16-39-ae69ef0-8.0
       file: ffmpeg-8.0-ae69ef0-win64-nonfree.7z

     We tested it with a Quadro P400 and driver 582.78. If you already have the
     "ffmpeg-autobuild" release of 12 March 2026, it works too (although it has no support
     for AMD graphics). Do not use the "ffmpeg-autobuild" releases between 15 March and
     6 April 2026: they write damaged MP4 files when the Intel graphics does the encoding.

   · GeForce GTX 600 or 700 (except the 750) and Quadro K...: their last driver (470)
     cannot run NVENC with any of these releases. The program records with the Intel
     graphics or the processor.

   Without an NVIDIA card, the latest release works.

2. Unzip what you downloaded: Windows 11 opens .7z files with a double click; on Windows 10
   you need a program such as 7-Zip (https://www.7-zip.org). Inside (sometimes in a "bin"
   folder) you will find:

       ffmpeg.exe
       ffprobe.exe

3. Copy THOSE TWO FILES into this very folder, next to this README:

       ...\Baioss\Record\tools\ffmpeg\

   Nothing else is needed: no installation, no system variables to change, no reboot.

4. (Optional, recommended) Check that the build works. Open this folder, type "cmd" in the
   address bar of File Explorer and press Enter; then run:

       ffmpeg.exe -hide_banner -version

   And, if you use DeckLink, also:

       ffmpeg.exe -hide_banner -f decklink -list_devices 1 -i dummy

   If that last one lists your cards, the build is the right one.

   And, if you record with NVIDIA (NVENC), also:

       ffmpeg.exe -hide_banner -f lavfi -i nullsrc -frames:v 1 -c:v h264_nvenc -f null -

   If no error appears, NVENC works. If it says "Driver does not support the required
   nvenc API version. Required: 13.1 Found: 13.0", the build is too new for your driver:
   update the driver or, if your card is in the second list of step 1, use FFmpeg 8.0
   (the release given in that step).

5. Open Baioss Record. If the files were copied correctly the warning is gone and you can
   record normally.


COMMON PROBLEMS
--------------------------------------------------------------------------------

· "I copied the files but it still warns me."
  Check that they are in THIS folder (not inside another "bin" subfolder) and that they are
  named exactly ffmpeg.exe and ffprobe.exe. Close the program completely and open it again.

· "Windows will not let me copy here."
  The installation grants write permission over this folder to users. If it still complains,
  copy the files as an administrator (right-click in File Explorer > Run as administrator)
  or ask your supplier for help.

· "ffprobe.exe is missing."
  Both files are needed: ffmpeg.exe does the recording and ffprobe.exe verifies that the
  recordings were closed correctly.

· "I have an NVIDIA card, but the program warns that it records with another encoder."
  Run the NVENC check of step 4. If it mentions "nvenc API version", follow step 1 for
  your card. If it says "incompatible client key", all the card's encoding sessions are
  in use: GeForce and entry-level Quadro cards allow 8 in the whole computer, and a remote
  desktop such as RustDesk or Parsec uses one too.


If you have any doubts, please contact your Baioss supplier.
