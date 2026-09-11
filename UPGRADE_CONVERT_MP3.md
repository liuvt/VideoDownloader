# Convert MP3 upgrade

Updated: 2026-09-11

## New features

### Advanced edit + merge
- Upload 2–10 MP3 files.
- Reorder files before merging.
- Add zero, one, or multiple intervals to remove from each individual file.
- Time input supports raw seconds, `MM:SS`, or `HH:MM:SS`.
- Overlapping intervals are normalized/merged server-side before FFmpeg processing.
- The remaining audio from every source file is normalized to 44.1 kHz stereo and concatenated into one MP3.

### Playback speed
- Slow down to 0.25x.
- Speed up to 4.00x.
- Uses FFmpeg `atempo`, including chained filters for factors outside a single safe 0.5x–2.0x stage.
- Pitch is preserved.

## VPS

The existing `AudioTools:FfmpegPath` setting is reused. Example production configuration:

```json
"AudioTools": {
  "FfmpegPath": "/usr/bin/ffmpeg"
}
```

Verify on the VPS:

```bash
which ffmpeg
ffmpeg -version
```

No additional audio package is required beyond FFmpeg.
