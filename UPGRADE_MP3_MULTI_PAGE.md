# Convert MP3 v4 – Single-page Multi Editor

## UI change
The old left-side tool selector is removed. `/convert-mp3` is now one unified workspace.

## Unified MP3 workflow
- Upload 1–10 MP3 files once.
- Reorder files before export.
- Per file choose:
  - **Remove areas**: add multiple red cut ranges.
  - **Keep one section**: choose one green range to keep.
- Drag START / END handles on the timeline, type exact timestamps, or use the current audio-player position.
- Apply final **volume 0–400%**.
- Apply final **playback speed 0.25x–4.00x** while preserving pitch with FFmpeg `atempo`.
- Export one MP3; when multiple files are selected they are joined in the visible order.

## White noise
White-noise generation is still available on the same `/convert-mp3` page as a separate panel because it does not require an input MP3.

## Backend changes
- The existing Merge pipeline now accepts 1–10 source files.
- `AudioFileEdit` now supports `KeptSegment` in addition to multiple `RemovedSegments`.
- Volume and speed can be applied together after the edited files are joined.
- FFmpeg remains server-side and uses the configured `FfmpegPath`.
