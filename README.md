# GPC Visualization

Windows/WPF MVP for loading GPC chromatogram data, displaying it, and exporting the displayed graph as PNG or SVG.

## Current MVP

- Open CSV, TSV, TXT files
- Read the first two numeric columns as X/Y data
- Read LabSolutions `[LC Chromatogram(...)]` text export sections
- Switch LabSolutions detector data when Detector A/B is selected
- Display the chromatogram with ScottPlot
- Multi-select data files while overlay mode is enabled
- Load a standard-curve JSON and convert retention time to molecular weight
- Edit graph title, axis labels, and X/Y axis ranges
- Change graph font, font size, plot frame, grid visibility, and display/export aspect ratio
- Toggle Y-axis tick-label visibility
- Save default graph formatting to a per-user config file and load it on startup
- Collapse sidebar settings sections to keep controls easier to scan
- Overlay multiple loaded data files
- Adjust plot-frame and line colors from a palette or custom hex color code
- Adjust line width, marker size, and legend name
- Show likely Mn, Mw, and PDI peak candidates from the data file when available, otherwise calculate them from converted molecular-weight data
- Export the displayed graph as a 300 dpi PNG or vector SVG with scaled export typography

## Usage

1. Run `GPC_Visualization`.
2. Click `CSVを開く` and choose a `.csv`, `.tsv`, or LabSolutions `.txt` file. Enable `重ね書き` first to select multiple files at once.
3. Click `較正曲線を開く` and choose a standard-curve `.json` file if molecular-weight conversion is needed.
4. Select the solvent and detector, then enable `分子量表示`.
5. Click `グラフを保存` to export a PNG or SVG.

Use `既定保存` to store the current formatting defaults. The app writes them to `%APPDATA%\GPC_Visualization\formatting_config.json` and loads them the next time it starts. Use `既定` to restore the saved defaults in the current session.

## Notes

The current molecular-weight conversion uses this polynomial:

```text
log10(M) = a*t^3 + b*t^2 + c*t + d
M = 10^log10(M)
```

Mn/Mw/PDI calculations are not implemented yet.

Molecular-weight display filters points outside `1` to `100000000` and plots the X axis as `log10(M)` with labels shown as molecular weights using superscript powers of 10.
The molecular-weight Y axis can be shown as either the raw signal or `dw/dlogM`.
For `dw/dlogM`, retention time is sorted descending before differencing to match the previous Python workflow and produce a positive distribution for ordinary GPC calibration curves.
When molecular-weight view is active, custom X Min/X Max values are entered as molecular weights, not `log10(M)`.
LabSolutions `Average Molecular Weight Table` `Total` rows are ignored; peak rows with positive Mn/Mw are sorted by `%` and displayed as candidates.
