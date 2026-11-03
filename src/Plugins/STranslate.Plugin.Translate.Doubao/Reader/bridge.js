// 注入 pdf.js 阅读器的桥接脚本：右侧镜像对照页、可见页上报、双向悬停高亮。
// 由 ReaderWindow 通过 AddScriptToExecuteOnDocumentCreatedAsync 在页面脚本之前执行。
// 注意：viewer.html 的 CSP 为 style-src 'self'，内联 <style> 会被拦截，样式统一通过 element.style 设置。
(() => {
  const post = message => window.chrome?.webview?.postMessage(message);

  const state = {
    mirror: true,
    pages: new Map(),      // 页码 -> [{ l, b, r, t, fs, tr }]，PDF 坐标与译文
    pending: new Set(),    // 正在翻译的页码
    labels: { pending: "…" },
    lastVisible: "",
  };
  const twinPages = new Map(); // 页码 -> { root, content, canvas, layer }

  let app = null;
  let container = null;
  let twin = null;
  let inner = null;
  let syncing = false;
  let layoutQueued = false;
  let needRender = false;
  let lastScrollHeight = 0;
  let mark = null;
  let hovered = null;

  // 禁止拖入其它文件，保证阅读器与译文始终对应同一份文档
  for (const type of ["dragover", "drop"]) {
    window.addEventListener(type, e => {
      e.preventDefault();
      e.stopImmediatePropagation();
    }, true);
  }

  document.addEventListener("DOMContentLoaded", () => {
    // 打开文件由宿主窗口负责，隐藏 pdf.js 自带的打开按钮
    const openButton = document.getElementById("secondaryOpenFile");
    if (openButton) openButton.style.display = "none";
  });

  document.addEventListener("webviewerloaded", () => {
    app = window.PDFViewerApplication;
    window.PDFViewerApplicationOptions.set("defaultUrl", "");
    app.initializedPromise.then(() => {
      const bus = app.eventBus;
      bus.on("documentloaded", () => post({ type: "loaded", pages: app.pagesCount, page: app.page }));
      bus.on("pagesinit", () => {
        setupTwin();
        queueLayout(true);
      });
      bus.on("pagerendered", e => {
        if (!e.isDetailView && !e.error) copyCanvas(e.pageNumber);
      });
      bus.on("scalechanging", () => queueLayout(true));
      bus.on("rotationchanging", () => queueLayout(true));
      bus.on("sidebarviewchanged", () => queueLayout(true));
      bus.on("updateviewarea", () => {
        queueLayout(false);
        reportVisible();
      });
      window.addEventListener("resize", () => queueLayout(true));
    });
  });

  // ---------- 镜像容器 ----------

  function setupTwin() {
    if (twin) return;
    container = document.getElementById("viewerContainer");
    twin = document.createElement("div");
    twin.id = "doubaoTwin";
    inner = document.createElement("div");
    inner.style.position = "relative";
    twin.append(inner);
    container.parentElement.append(twin);

    container.addEventListener("scroll", () => syncScroll(container, twin));
    twin.addEventListener("scroll", () => syncScroll(twin, container));
    container.addEventListener("mousemove", onSourceMouseMove);
    container.addEventListener("mouseleave", () => setHover(null));
    applyMirror();
  }

  function applyMirror() {
    if (!twin) return;
    container.style.right = state.mirror ? "50%" : "";
    Object.assign(twin.style, {
      display: state.mirror ? "block" : "none",
      position: "absolute",
      top: getComputedStyle(container).top,
      bottom: "0",
      left: "50%",
      right: "0",
      overflow: "auto",
      background: "var(--body-bg-color)",
      borderLeft: "1px solid rgba(128, 128, 128, 0.35)",
    });
    // 让 pdf.js 按新的容器宽度重新计算「自动缩放」
    app.eventBus.dispatch("resize", { source: window });
    queueLayout(true);
  }

  // 两侧内容高度一致，滚动位置 1:1 同步
  function syncScroll(from, to) {
    if (syncing || !state.mirror) return;
    syncing = true;
    to.scrollTop = from.scrollTop;
    requestAnimationFrame(() => (syncing = false));
  }

  function queueLayout(rerender) {
    needRender ||= rerender;
    if (layoutQueued) return;
    layoutQueued = true;
    requestAnimationFrame(() => {
      layoutQueued = false;
      layout();
    });
  }

  // 按左侧页面的实际位置与尺寸摆放镜像页；仅在缩放、尺寸变化时全量重排
  function layout() {
    if (!twin || !state.mirror || !app?.pdfViewer) return;
    const scrollHeight = container.scrollHeight;
    if (!needRender && scrollHeight === lastScrollHeight) return;
    lastScrollHeight = scrollHeight;
    const rerender = needRender;
    needRender = false;

    inner.style.height = `${scrollHeight}px`;
    const origin = container.getBoundingClientRect().top - container.scrollTop;
    const twinWidth = twin.clientWidth;
    let maxWidth = 0;

    for (let i = 1; i <= app.pdfViewer.pagesCount; i++) {
      const div = app.pdfViewer.getPageView(i - 1)?.div;
      if (!div) continue;
      const page = ensureTwinPage(i);
      const width = div.offsetWidth;
      maxWidth = Math.max(maxWidth, width);
      Object.assign(page.root.style, {
        top: `${div.getBoundingClientRect().top - origin}px`,
        left: `${Math.max(8, (twinWidth - width) / 2)}px`,
        width: `${width}px`,
        height: `${div.offsetHeight}px`,
      });
      Object.assign(page.content.style, {
        left: `${div.clientLeft}px`,
        top: `${div.clientTop}px`,
        width: `${div.clientWidth}px`,
        height: `${div.clientHeight}px`,
      });
      if (rerender && (state.pages.has(i) || state.pending.has(i))) renderPage(i);
    }
    inner.style.width = `${Math.max(twinWidth, maxWidth + 16)}px`;
  }

  function ensureTwinPage(n) {
    let page = twinPages.get(n);
    if (page) return page;

    const root = document.createElement("div");
    Object.assign(root.style, {
      position: "absolute",
      background: "#fff",
      boxShadow: "0 1px 4px rgba(0, 0, 0, 0.25)",
    });
    const content = document.createElement("div");
    Object.assign(content.style, { position: "absolute", overflow: "hidden" });
    const canvas = document.createElement("canvas");
    Object.assign(canvas.style, { position: "absolute", inset: "0", width: "100%", height: "100%" });
    const layer = document.createElement("div");
    Object.assign(layer.style, { position: "absolute", inset: "0" });
    content.append(canvas, layer);
    root.append(content);
    inner.append(root);

    page = { root, content, canvas, layer };
    twinPages.set(n, page);
    return page;
  }

  // 复制左侧渲染结果作为镜像页底图，图片、代码、表格等未翻译内容原样可见
  function copyCanvas(n) {
    if (!twin) return;
    const source = app.pdfViewer.getPageView(n - 1)?.div?.querySelector(".canvasWrapper canvas");
    if (!source?.width || !source.height) return;
    const { canvas } = ensureTwinPage(n);
    canvas.width = source.width;
    canvas.height = source.height;
    canvas.getContext("2d").drawImage(source, 0, 0);
  }

  // ---------- 译文渲染 ----------

  function renderPage(n) {
    const page = twinPages.get(n);
    const view = app?.pdfViewer?.getPageView(n - 1);
    if (!page || !view?.viewport) return;

    page.layer.replaceChildren();
    if (state.pending.has(n) && !state.pages.has(n)) {
      const badge = document.createElement("div");
      badge.textContent = state.labels.pending;
      Object.assign(badge.style, {
        position: "absolute", top: "8px", right: "8px", padding: "2px 10px", borderRadius: "10px",
        background: "rgba(0, 0, 0, 0.6)", color: "#fff", font: "12px 'Microsoft YaHei UI', sans-serif",
      });
      page.layer.append(badge);
    }

    const paragraphs = state.pages.get(n) ?? [];
    paragraphs.forEach((p, index) => {
      const [x1, y1] = view.viewport.convertToViewportPoint(p.l, p.b);
      const [x2, y2] = view.viewport.convertToViewportPoint(p.r, p.t);
      // PdfPig 的文字框不含下伸部，按字号略微外扩，完整遮住原文
      const fontPx = (p.fs || 10) * view.viewport.scale;
      const padTop = fontPx * 0.15;
      const padBottom = fontPx * 0.3;
      const box = document.createElement("div");
      box.className = "doubao-translation";
      box.textContent = p.tr;
      box.title = p.tr;
      Object.assign(box.style, {
        position: "absolute",
        left: `${Math.min(x1, x2) - 1}px`,
        top: `${Math.min(y1, y2) - padTop}px`,
        width: `${Math.abs(x2 - x1) + 2}px`,
        height: `${Math.abs(y2 - y1) + padTop + padBottom}px`,
        boxSizing: "border-box",
        padding: "0 1px",
        overflow: "hidden",
        background: "#fff",
        color: "#111",
        lineHeight: "1.3",
        fontFamily: "'Microsoft YaHei UI', 'PingFang SC', 'Noto Sans CJK SC', sans-serif",
        wordBreak: "break-word",
        cursor: "default",
      });
      box.addEventListener("mouseenter", () => setHover({ n, index }));
      box.addEventListener("mouseleave", () => setHover(null));
      page.layer.append(box);
      fitText(box, Math.max(6, fontPx));
      p.box = box;
    });
  }

  // 从原文字号开始二分查找能完整放下译文的最大字号
  function fitText(box, maxSize) {
    const fits = size => {
      box.style.fontSize = `${size}px`;
      return box.scrollHeight <= box.clientHeight + 1;
    };
    box.dataset.overflow = "";
    if (fits(maxSize)) return;
    let lo = 6, hi = maxSize;
    for (let i = 0; i < 7; i++) {
      const mid = (lo + hi) / 2;
      if (fits(mid)) lo = mid; else hi = mid;
    }
    // 最小字号仍放不下时，悬停可展开查看全文
    if (!fits(lo)) box.dataset.overflow = "1";
  }

  // ---------- 悬停联动 ----------

  function onSourceMouseMove(e) {
    const pageDiv = e.target.closest?.(".page");
    const n = Number(pageDiv?.dataset.pageNumber);
    const paragraphs = state.pages.get(n);
    const view = app.pdfViewer.getPageView(n - 1);
    if (!paragraphs || !view?.viewport) return setHover(null);

    const rect = pageDiv.getBoundingClientRect();
    const [x, y] = view.viewport.convertToPdfPoint(e.clientX - rect.left - pageDiv.clientLeft, e.clientY - rect.top - pageDiv.clientTop);
    const index = paragraphs.findIndex(p => x >= p.l && x <= p.r && y >= p.b && y <= p.t);
    setHover(index >= 0 ? { n, index } : null);
  }

  function setHover(target) {
    if (hovered?.n === target?.n && hovered?.index === target?.index) return;
    const previous = hovered && state.pages.get(hovered.n)?.[hovered.index]?.box;
    if (previous) {
      Object.assign(previous.style, { outline: "", zIndex: "", boxShadow: "" });
      if (previous.dataset.height) previous.style.height = previous.dataset.height;
    }
    reader.clear();
    hovered = target;
    if (!target) return;

    const p = state.pages.get(target.n)?.[target.index];
    if (!p) return;
    reader.highlight(target.n, p.l, p.b, p.r, p.t);
    if (p.box) {
      Object.assign(p.box.style, { outline: "2px solid rgba(255, 160, 0, 0.85)", zIndex: "5" });
      if (p.box.dataset.overflow) {
        p.box.dataset.height = p.box.style.height;
        Object.assign(p.box.style, { height: "auto", boxShadow: "0 2px 8px rgba(0, 0, 0, 0.3)" });
      }
    }
  }

  // ---------- 可见页上报 ----------

  function reportVisible() {
    if (!container || !app?.pdfViewer) return;
    const current = app.page;
    const top = container.getBoundingClientRect().top;
    const bottom = top + container.clientHeight;
    const visible = [];
    for (let n = Math.max(1, current - 2); n <= Math.min(app.pagesCount, current + 2); n++) {
      const rect = app.pdfViewer.getPageView(n - 1)?.div?.getBoundingClientRect();
      if (rect && rect.bottom > top && rect.top < bottom) visible.push(n);
    }
    const key = `${current}:${visible.join(",")}`;
    if (key === state.lastVisible) return;
    state.lastVisible = key;
    post({ type: "visible", page: current, pages: visible });
  }

  // ---------- 宿主调用接口 ----------

  const reader = (window.doubaoReader = {
    setLabels(labels) {
      Object.assign(state.labels, labels);
    },

    setMirror(enabled) {
      state.mirror = enabled;
      applyMirror();
    },

    setPending(n) {
      if (state.pages.has(n)) return;
      state.pending.add(n);
      renderPage(n);
    },

    setPage(n, paragraphs) {
      state.pending.delete(n);
      state.pages.set(n, paragraphs);
      renderPage(n);
    },

    // 切换目标语言后清空译文并重新上报可见页
    reset() {
      state.pages.clear();
      state.pending.clear();
      twinPages.forEach(page => page.layer.replaceChildren());
      state.lastVisible = "";
      reportVisible();
    },

    // 高亮左侧指定页的段落，坐标为 PDF 用户空间
    highlight(n, left, bottom, right, top) {
      this.clear();
      const view = app?.pdfViewer?.getPageView(n - 1);
      if (!view?.viewport || !view.div) return;
      // pdf.js 6 移除了 convertToViewportRectangle，按两个角点分别换算
      const [x1, y1] = view.viewport.convertToViewportPoint(left, bottom);
      const [x2, y2] = view.viewport.convertToViewportPoint(right, top);
      mark = document.createElement("div");
      Object.assign(mark.style, {
        position: "absolute",
        zIndex: "10",
        pointerEvents: "none",
        borderRadius: "2px",
        background: "rgba(255, 196, 0, 0.30)",
        outline: "2px solid rgba(255, 160, 0, 0.85)",
        left: `${Math.min(x1, x2) - 2}px`,
        top: `${Math.min(y1, y2) - 2}px`,
        width: `${Math.abs(x2 - x1) + 4}px`,
        height: `${Math.abs(y2 - y1) + 4}px`,
      });
      view.div.append(mark);
    },

    clear() {
      mark?.remove();
      mark = null;
    },
  });
})();
