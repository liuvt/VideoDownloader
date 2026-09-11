# Convert MP3 – Visual timeline upgrade

## New UI

- Visual cut timeline for **Remove a section**, **Keep a section**, and **Edit & merge**.
- Browser reads MP3 duration locally with an object URL; the file is not uploaded just to build the timeline.
- Drag **START** and **END** handles directly on the timeline.
- Existing cut regions are shown in red; click a region/chip to edit it again.
- Multiple cut regions per source file are supported.
- Exact time inputs remain available for precision and fallback.
- Each merge source has a local audio player.
- **Set START at player** / **Set END at player** copy the current player position to the active cut.
- **Play cut** previews only the selected interval.
- Responsive controls are included for smaller screens.

## Processing

The timeline only changes the editor UI. Final MP3 processing still runs server-side through the existing `AudioToolService` and FFmpeg pipeline.
