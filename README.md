# GPC Visualization

Windows/WPF MVP for loading GPC chromatogram data, displaying it, and exporting the displayed graph as PNG.

## Current MVP

- Open CSV, TSV, TXT files
- Read the first two numeric columns as X/Y data
- Read LabSolutions `[LC Chromatogram(...)]` text export sections
- Display the chromatogram with ScottPlot
- Export the displayed graph as PNG

## Usage

1. Run `GPC_Visualization`.
2. Click `CSVを開く` and choose a `.csv`, `.tsv`, or LabSolutions `.txt` file.
3. Review the displayed chromatogram.
4. Click `グラフを保存` to export a PNG.

## Notes

Calibration curve JSON files are not used in this MVP yet. They are reserved for the next phase, where retention time will be converted to molecular weight and Mn/Mw/PDI will be calculated.
