// Renders a PDF to <canvas> with PDF.js and draws absolutely-positioned highlight
// boxes on top for AI-extracted fields, color-coded by confidence. Positions are
// stored/passed as percentages of page width/height (see FieldLocator.cs), so the
// overlay stays correctly aligned regardless of zoom/scale.
//
// PDF.js is loaded on demand from cdnjs - bump PDFJS_VERSION if you want a newer
// release. Swap this for a locally-hosted copy if the deployment target has no
// internet access to cdnjs.
const PDFJS_VERSION = "4.6.82";

let pdfjsLibPromise = null;
function loadPdfJs() {
    if (!pdfjsLibPromise) {
        pdfjsLibPromise = import(`https://cdnjs.cloudflare.com/ajax/libs/pdf.js/${PDFJS_VERSION}/pdf.min.mjs`)
            .then(lib => {
                lib.GlobalWorkerOptions.workerSrc =
                    `https://cdnjs.cloudflare.com/ajax/libs/pdf.js/${PDFJS_VERSION}/pdf.worker.min.mjs`;
                return lib;
            });
    }
    return pdfjsLibPromise;
}

// One viewer instance per element id pair, keyed on canvasContainerId, so multiple
// review screens on the page (unlikely, but harmless) don't clobber each other.
const viewers = {};

export async function init(canvasContainerId, overlayId, pdfUrl, dotNetRef) {
    const pdfjsLib = await loadPdfJs();
    const loadingTask = pdfjsLib.getDocument(pdfUrl);
    const pdfDoc = await loadingTask.promise;

    viewers[canvasContainerId] = { pdfDoc, overlayId, dotNetRef, scale: 1.35 };
    return pdfDoc.numPages;
}

export async function renderPage(canvasContainerId, pageNumber, highlightsJson) {
    const state = viewers[canvasContainerId];
    if (!state) return;

    const container = document.getElementById(canvasContainerId);
    const overlay = document.getElementById(state.overlayId);
    if (!container || !overlay) return;

    const page = await state.pdfDoc.getPage(pageNumber);
    const viewport = page.getViewport({ scale: state.scale });

    container.innerHTML = "";
    const canvas = document.createElement("canvas");
    canvas.width = viewport.width;
    canvas.height = viewport.height;
    container.appendChild(canvas);

    const ctx = canvas.getContext("2d");
    await page.render({ canvasContext: ctx, viewport }).promise;

    overlay.style.width = viewport.width + "px";
    overlay.style.height = viewport.height + "px";
    overlay.innerHTML = "";

    const highlights = highlightsJson ? JSON.parse(highlightsJson) : [];
    for (const h of highlights.filter(h => h.page === pageNumber)) {
        const box = document.createElement("div");
        box.className = "pdf-highlight pdf-highlight-" + h.level;
        box.style.left = (h.leftPct * 100) + "%";
        box.style.top = (h.topPct * 100) + "%";
        box.style.width = (h.widthPct * 100) + "%";
        box.style.height = (h.heightPct * 100) + "%";
        box.title = `${h.label} — ${Math.round(h.confidence * 100)}% confidence`;
        box.dataset.fieldName = h.fieldName;
        box.addEventListener("click", () => {
            const s = viewers[canvasContainerId];
            s?.dotNetRef?.invokeMethodAsync("OnHighlightClicked", h.fieldName);
        });
        overlay.appendChild(box);
    }
}

/// Briefly pulses the box for one field (used when the side panel is clicked) and
/// scrolls it into view within the PDF pane.
export function flashField(canvasContainerId, fieldName) {
    const state = viewers[canvasContainerId];
    if (!state) return;
    const overlay = document.getElementById(state.overlayId);
    const box = overlay?.querySelector(`[data-field-name="${CSS.escape(fieldName)}"]`);
    if (!box) return;

    box.scrollIntoView({ behavior: "smooth", block: "center" });
    box.classList.add("pdf-highlight-flash");
    setTimeout(() => box.classList.remove("pdf-highlight-flash"), 1200);
}

export function setZoom(canvasContainerId, delta) {
    const state = viewers[canvasContainerId];
    if (!state) return;
    state.scale = Math.min(3, Math.max(0.5, state.scale + delta));
    return state.scale;
}

export function dispose(canvasContainerId) {
    delete viewers[canvasContainerId];
}
