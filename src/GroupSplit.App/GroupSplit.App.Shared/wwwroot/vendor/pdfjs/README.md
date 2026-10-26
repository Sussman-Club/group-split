# PDF.js 6.4.299

Self-hosted browser renderer from Mozilla's pdfjs-dist npm package. Apache-2.0;
see LICENSE and the licenses in wasm/ and standard_fonts/. Loaded only for PDF previews.

Package integrity: sha512-AVl138zALtfaAPvADulE0PZThbYzCBS79nL4pOSL/6Sm/4AH5A21BD9VHt97OlCuzJuCpmeZtAtkinisF4Vb1g==

The vendored subset contains the minified renderer and worker, character maps,
standard fonts, and image decoders. No viewer shell, source maps or PDF scripting sandbox.
PDF JavaScript evaluation is disabled by the preview module.
