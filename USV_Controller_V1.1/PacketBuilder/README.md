PacketBuilder
=============

This file (`PacketBuilder.cs`) contains the original `ScaleAxis` and `BuildPacket` methods
extracted from `MainWindow.xaml.cs` into a `partial` class file.

Purpose:
- Keep packet construction logic grouped together.
- No logic changes were made; methods are verbatim from the previous single-file layout.
- The partial-class approach preserves access to private state (for example `_gamepad`) in `MainWindow`.