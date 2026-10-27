# PDF preview dependency

The receipt preview uses Mozilla's PDF.js `pdfjs-dist` npm package, pinned to 6.4.299.
The version and SHA-512 integrity are recorded in `build/PdfJs.targets`.

Normal `dotnet build` and `dotnet publish` restore it automatically before Razor static
web assets are discovered. No Node or npm installation is required: the .NET 10 helper
in `tools/restore-pdfjs.cs` downloads the package from the npm registry, verifies its
integrity, and copies the renderer, worker, character maps, standard fonts, image
decoders, and license notices into the ignored `wwwroot/vendor/pdfjs/` directory.
The existing browser URLs remain the same, and receipt data stays on our origin.
PDF JavaScript evaluation is disabled by the preview module.

The downloaded archive is cached under the shared project's `obj/pdfjs/` directory.
Subsequent builds reuse generated assets; deleting the asset directory regenerates
them from the verified cache. A clean checkout needs registry access on its first
build. A failed download or integrity check fails the build rather than publishing
a broken preview. To upgrade, change both the version and integrity together, then
build and check PDF paging, zoom, international text, and the download fallback.

PDF.js is Apache-2.0 licensed. Its package also includes notices for the bundled
fonts and codecs; these are copied and published with the runtime assets.
