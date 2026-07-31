# PdfOutlineFonts third-party notices

This feature embeds an unmodified Ghostscript native library.

- Product: GPL Ghostscript
- Version: 10.07.1
- Copyright: Artifex Software, Inc. and contributors
- Project: https://ghostscript.com/
- Source: https://github.com/ArtifexSoftware/ghostpdl
- License: GNU Affero General Public License version 3
- License text: https://www.gnu.org/licenses/agpl-3.0.html

Ghostscript is loaded only by the PDF outline feature. The x86 application embeds
`gsdll32.dll`; the x64 application embeds `gsdll64.dll`. The ARM64 application
does not embed or load an incompatible native library.
