# S-Class UI Style Guide (Comprehensive)

This guide documents the UI "DNA" of the S-Class ground control station. It is intended to be used as a reference when styling new WPF applications to maintain the same technical, high-contrast, modern control interface.

## 1. Theme Philosophy
- **Base Style**: Modern Dark control console.
- **Contrast**: High-visibility text/accents against deep dark backgrounds.
- **Modularity**: Card-based architecture using nested `Border` elements for containers.
- **Visual Cues**: Uses specific colors, status dots (ellipses), and simple iconography (emojis) for fast status identification.

## 2. Color Palette

| Usage | Hex Code | Purpose |
| :--- | :--- | :--- |
| **Window Background** | `#080D1A` or `#0B111E` | Main application shell |
| **Container Background** | `#111827` | Content containers |
| **Input Background** | `#0F172A` | Textboxes, status bars |
| **Text Primary** | `#F8FAFC` | Main labels, headers |
| **Text Secondary** | `#94A3B8` | Subtitles, hints, inactive labels |
| **Accent Blue** | `#0284C7` | Primary interactive/action items |
| **Success/Active** | `#34D399` | Positive status, success signals |
| **Danger/Error** | `#991B1B` | Destructive actions, errors |
| **Border/Separator** | `#334155` | Borders, subtle dividers |
| **Hover/Interactive** | `#334155` | Mouse-over states |

## 3. Layout Conventions

- **Containers (`Border` Styling)**: Always use `CornerRadius="5"` to `"10"` and `BorderThickness="1"` with a `BorderBrush` from the `#1F2937` / `#334155` range to establish hierarchy.
- **Spacing**: Use consistent `Margin` and `Padding`. Panels often use `Padding="12"` or `"16"` to separate content from borders.
- **Grids**: Use `Grid` row/column definitions to structure layouts, particularly for complex controls or video display areas.
- **Overlays**: Use transparent `Border` elements or `Popup` controls for interactive overlays on top of video feeds (e.g., PTZ controls).

## 4. Component Styles

### Buttons
All buttons use custom templates for modern, rounded aesthetics.

- **`ActionButton` (Default)**: Background `#1E293B`, Border `#334155`.
- **`AccentButton` (Primary)**: Background `#0284C7`, Foreground `#FFFFFF`.
- **`DangerButton` (Destructive)**: Background `#991B1B`, Foreground `#FFFFFF`.
- **`PtzButton` (Specialized)**: Compact, square, borderless-feeling buttons (`38x38`) for directional controls (`◀`, `▲`, etc.).

### Inputs
- **`ModernTextBox`**: Dark background (`#0F172A`) with high-contrast text (`#F8FAFC`) and thin borders (`#334155`).

### Status Indicators
- **Dots**: Use `Ellipse` elements with specific colors (`Fill`) to represent state:
  - Online/Active: `#34D399`
  - Offline/Disconnected: `#64748B`

## 5. Typography
- **Monospaced (Technical)**: `Consolas` is the mandatory font for clocks, status chips, resolution data, and technical identifiers.
- **Standard (Interface)**: System default (Segoe UI), `Bold`/`SemiBold` for headers and buttons, to ensure readability.

## 6. Iconography
- **Emojis**: Use simple Unicode emojis for lightweight visual cues (e.g., `📡` for radar, `📹` for camera). This avoids overhead of complex image assets.
