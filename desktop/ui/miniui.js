(function () {
  "use strict";
  var mode = "optical", format = "original", busy = false;
  var folders = {}, selections = { optical: [] };
  var remoteCount = 0;
  var pending = [];
  var qrToken = null, cameraRunning = false, openWhenDone = true, lastNativeError = "";
  var outputRootShown = false, lastCameraState = "";
  function id(name) { return document.getElementById(name); }
  function each(selector, fn) { document.querySelectorAll(selector).forEach(fn); }
  function text(name, value) { id(name).textContent = String(value); }
  function message(value) { text("toast", value); id("toast").hidden = false; }
  function request(path, body, done) {
    var arg = { path: path };
    if (body !== null) arg.body = body;
    var reply = JSON.parse(uiHost.call(JSON.stringify(arg)));
    if (reply.error) { done(null, reply.error); return; }
    pending.push({ id: reply.job, done: done });
  }
  setInterval(function () {
    var old = pending; pending = [];
    old.forEach(function (job) {
      var reply = JSON.parse(uiHost.call(JSON.stringify({ poll: job.id })));
      if (reply.pending) pending.push(job);
      else job.done(reply.result, reply.error);
    });
  }, 40);
  function bytes(n) { return n < 1024 ? n + " B" : n < 1048576 ? (n / 1024).toFixed(1) + " KB" : (n / 1048576).toFixed(1) + " MB"; }
  function supported(file, page) { return page === "optical"; }
  function choosePage(page) {
    mode = page;
    each("[data-page-panel]", function (el) { el.hidden = el.getAttribute("data-page-panel") !== page; });
    each("[data-page]", function (el) {
      var active = el.getAttribute("data-page") === page;
      el.classList.toggle("is-active", active);
      if (active) el.setAttribute("aria-current", "page"); else el.removeAttribute("aria-current");
    });
  }
  function updateActions() {
    if (format !== "original" && (!folders.optical || !folders.optical.files.some(function (f) {
      return selections.optical.indexOf(f.name) >= 0 && supported(f, format);
    }))) format = "original";
    each(".choose-files, .choose-folder, [data-selection-action]", function (el) { el.disabled = busy; });
    id("showRemoteQr").disabled = busy || remoteCount === 0 || selections.optical.length === 0;
    id("readRemoteCamera").disabled = busy || remoteCount === 0;
    id("showQr").disabled = busy || selections.optical.length === 0;
    id("readLocalCamera").disabled = busy;
    each("[data-transfer-format]", function (el) {
      var kind = el.getAttribute("data-transfer-format");
      var available = kind === "original" || (folders.optical && folders.optical.files.some(function (f) {
        return selections.optical.indexOf(f.name) >= 0 && supported(f, kind);
      }));
      el.disabled = busy || !available;
      el.classList.toggle("is-active", kind === format);
    });
  }
  function span(value, cls) { var el = document.createElement("span"); el.className = cls; el.textContent = value; return el; }
  function summary(page) {
    var root = document.querySelector('[data-page-panel="' + page + '"]');
    var selected = folders[page].files.filter(function (f) { return selections[page].indexOf(f.name) >= 0; });
    root.querySelector("[data-summary-count]").textContent = String(selected.length);
    root.querySelector("[data-summary-size]").textContent = bytes(selected.reduce(function (sum, f) { return sum + f.size; }, 0));
    var types = root.querySelector("[data-summary-types]"); types.replaceChildren();
    ["PDF", "Word", "Excel", "PowerPoint", "Text", "Other"].forEach(function (kind) {
      var count = selected.filter(function (f) { return f.kind === kind; }).length;
      if (count) types.appendChild(span(kind + " " + count, "summary-pill"));
    });
  }
  function render(page) {
    var folder = folders[page];
    if (!folder) return;
    var root = document.querySelector('[data-page-panel="' + page + '"]');
    root.querySelector("[data-folder-path]").textContent = folder.path || "ファイルまたはフォルダを選んでください";
    summary(page);
    var list = id(page + "Files"); list.replaceChildren();
    folder.files.forEach(function (file) {
      if (!supported(file, page)) return;
      var row = document.createElement("button"); row.className = "file-row";
      var selected = selections[page].indexOf(file.name) >= 0;
      row.classList.toggle("is-selected", selected);
      var nameCell = span("", "file-name");
      row.appendChild(nameCell);
      row.appendChild(span(bytes(file.size), "file-size"));
      row.appendChild(span(String(file.modifiedUtc || "").slice(0, 16).replace("T", " "), "file-date"));
      list.appendChild(row);
      var select = row;
      function toggle() {
        if (busy) return;
        var index = selections[page].indexOf(file.name);
        if (index >= 0) selections[page].splice(index, 1); else selections[page].push(file.name);
        var active = index < 0;
        row.classList.toggle("is-selected", active); select.setAttribute("aria-checked", active ? "true" : "false");
        nameCell.firstChild.textContent = active ? "[x] " : "[ ] ";
        summary(page); updateActions();
      }
      row.type = "button"; nameCell.appendChild(span(selected ? "[x] " : "[ ] ", ""));
      nameCell.appendChild(span(file.badge, "file-badge")); nameCell.appendChild(span(file.name, "file-name-text"));
      select.setAttribute("role", "checkbox"); select.setAttribute("aria-label", file.name + " を選ぶ");
      select.setAttribute("aria-checked", selected ? "true" : "false"); select.addEventListener("click", toggle);
    });
    if (!list.firstChild) list.appendChild(span("ファイルはありません", "empty-state"));
    updateActions();
  }
  function acceptFolder(page, folder) {
    folders[page] = folder;
    selections[page] = folder.files.filter(function (f) { return supported(f, page); }).map(function (f) { return f.name; });
    render(page);
  }
  function refresh(page) {
    request("/api/folder?mode=" + page, null, function (result, error) {
      if (error) message(error); else acceptFolder(page, result);
    });
  }
  function pick(page, files) {
    if (busy) return;
    busy = true; updateActions();
    request(files ? "/api/pick-files" : "/api/pick-folder?mode=" + page, { mode: page }, function (result, error) {
      busy = false;
      if (error) message(error); else if (!result.cancelled) acceptFolder(page, result.folder);
      updateActions();
    });
  }
  function nativeCall(value) {
    var reply = JSON.parse(uiHost.call(JSON.stringify(value)));
    if (reply.error) throw new Error(reply.error);
    return reply.result;
  }
  function sizeQr(delta) {
    var requested = Math.max(300, Math.min(1200, Number(id("qrDisplaySize").value) + delta));
    id("qrDisplaySize").value = String(requested);
    var viewport = uiHost.viewport();
    var actual = Math.max(100, Math.min(requested, viewport.width - 80, viewport.height - 180));
    id("qrFrame").style.width = actual + "px"; id("qrFrame").style.height = actual + "px";
    text("qrSizeSetting", requested + " px"); text("qrSizeOutput", actual + " px");
  }
  function stopQr() {
    nativeCall({ native: "stop-display" });
    id("qrOverlay").hidden = true;
    if (qrToken) request("/api/optical/stop", { token: qrToken }, function (r, e) { if (e) message(e); });
    qrToken = null;
  }
  function startQr() {
    if (busy || !selections.optical.length) return;
    busy = true; updateActions();
    request("/api/optical/start", { files: selections.optical.slice(), format: format,
      frameBytes: Number(id("frameAmount").value), framesPerSecond: Number(id("frameRate").value),
      errorCorrection: id("errorCorrection").value }, function (result, error) {
        busy = false; updateActions();
        if (error) { message(error); return; }
        qrToken = result.token;
        try {
          nativeCall({ native: "display", token: qrToken, fps: result.framesPerSecond });
          id("toast").hidden = true; text("qrProgress", "QR を準備しています"); sizeQr(0); id("qrOverlay").hidden = false; id("closeQr").focus();
        } catch (e) { stopQr(); message(e.message); }
      });
  }
  function cameraToggle() {
    try {
      if (cameraRunning) { nativeCall({ native: "stop-camera" }); cameraRunning = false; id("cameraVideo").hidden = true;
        each("#cameraSettings select, #receiveOpenFolder", function (el) { el.disabled = false; });
        text("statusCamera", "停止"); text("cameraLabel", "カメラを停止しました"); }
      else {
        nativeCall({ native: "start-camera", width: Number(id("cameraWidth").value), fps: Number(id("cameraFps").value),
          workers: Number(id("cameraWorkers").value), openWhenDone: openWhenDone });
        cameraRunning = true; id("cameraVideo").hidden = false;
        each("[data-optical-view]", function (el) { el.hidden = el.getAttribute("data-optical-view") !== "receive"; });
        each("[data-view]", function (el) { el.classList.toggle("is-active", el.getAttribute("data-view") === "receive"); });
      }
      text("readLocalCamera", cameraRunning ? "カメラを停止" : "この PC のカメラで読む");
    } catch (e) { message(e.message); }
  }
  setInterval(function () {
    try {
      var status = nativeCall({ native: "status" });
      if (status.error && status.error !== lastNativeError) { message(status.error); lastNativeError = status.error; }
      if (qrToken) text("qrProgress", status.displayFrames + " 枚表示 / 送信中");
      if (!outputRootShown) {
        text("receiveOutputPath", status.outputRoot + "/受信名_日時"); outputRootShown = true;
      }
      var camera = status.camera;
      if (!camera) return;
      var cameraState = JSON.stringify(camera);
      if (cameraState === lastCameraState) return;
      lastCameraState = cameraState;
      cameraRunning = camera.running;
      text("readLocalCamera", cameraRunning ? "カメラを停止" : "この PC のカメラで読む");
      each("#cameraSettings select, #receiveOpenFolder", function (el) { el.disabled = cameraRunning; });
      text("statusCamera", camera.running ? "受信中" : "停止"); id("statusCamera").classList.toggle("is-muted", !camera.running);
      text("cameraLabel", camera.running ? "カメラ受信中" : camera.error ? "カメラが停止しました" : "受信終了");
      text("cameraActual", camera.device + " / " + camera.width + "×" + camera.height + " / 要求 " + camera.requestedFps + " fps / 撮影 " + camera.captured + " 枚・解析 " + camera.decoded + " 枚");
      if (camera.error && camera.error !== lastNativeError) { message(camera.error); lastNativeError = camera.error; }
      var result = camera.receive;
      if (result) {
        text("receiveState", result.complete ? "保存完了" : "受信中");
        var percent = Math.round(result.progress * 100);
        id("receiveProgressBar").style.width = percent + "%"; id("receiveProgress").setAttribute("aria-valuenow", String(percent));
        text("receiveFrames", result.framesCollected + " 枚"); text("receiveDetail", result.solvedBlocks + " / " + result.sourceBlocks + " ブロック");
        text("receiveRate", result.kilobytesPerSecond.toFixed(1) + " KB/s");
        text("receiveEta", result.complete ? "完了" : "受信継続中");
        if (result.label) text("receiveItem", result.label);
        if (result.outputPath) text("receiveOutputPath", result.outputPath);
        if (result.openError) message(result.openError);
      }
    } catch (e) { if (e.message !== lastNativeError) { lastNativeError = e.message; message(e.message); } }
  }, 250);
  function remote(action) {
    if (busy) return;
    busy = true; updateActions();
    request("/api/remotes/command", { action: action, files: selections.optical.slice(), format: format,
      frameBytes: Number(id("frameAmount").value), framesPerSecond: Number(id("frameRate").value),
      errorCorrection: id("errorCorrection").value, displaySize: Number(id("qrDisplaySize").value) }, function (result, error) {
        busy = false; updateActions(); message(error || (result.sent + " 台へ操作を送りました"));
      });
  }
  each("[data-page]", function (el) { el.addEventListener("click", function () { if (!busy) choosePage(el.getAttribute("data-page")); }); });
  each(".choose-files", function (el) { el.addEventListener("click", function () { pick(el.getAttribute("data-picker-mode"), true); }); });
  each(".choose-folder", function (el) { el.addEventListener("click", function () { pick(el.getAttribute("data-picker-mode"), false); }); });
  each("[data-selection-action]", function (el) { el.addEventListener("click", function () {
    if (busy) return;
    var page = el.getAttribute("data-selection-mode");
    if (!folders[page]) return;
    selections[page] = el.getAttribute("data-selection-action") === "all" ? folders[page].files.filter(function (f) { return supported(f, page); }).map(function (f) { return f.name; }) : [];
    render(page);
  }); });
  each("[data-transfer-format]", function (el) { el.addEventListener("click", function () { format = el.getAttribute("data-transfer-format"); updateActions(); }); });
  each("[data-theme-toggle]", function (el) { el.textContent = "明 / 暗"; el.addEventListener("click", function () {
    var dark = document.documentElement.getAttribute("data-theme") !== "dark";
    document.documentElement.setAttribute("data-theme", dark ? "dark" : "light");
  }); });
  each("[data-view]", function (el) { el.addEventListener("click", function () {
    var view = el.getAttribute("data-view");
    each("[data-optical-view]", function (panel) { panel.hidden = panel.getAttribute("data-optical-view") !== view; });
    each("[data-view]", function (button) { button.classList.toggle("is-active", button === el); });
  }); });
  function frameCapacity() {
    var maximum = { L: 2953, M: 2331, Q: 1663, H: 1273 }[id("errorCorrection").value];
    var last = "500";
    each("#frameAmount option", function (el) { el.disabled = Number(el.value) > maximum; if (!el.disabled) last = el.value; });
    if (Number(id("frameAmount").value) > maximum) id("frameAmount").value = last;
  }
  id("errorCorrection").addEventListener("change", frameCapacity);
  id("showQr").addEventListener("click", startQr);
  id("closeQr").addEventListener("click", stopQr);
  id("smallerQr").addEventListener("click", function () { sizeQr(-50); });
  id("largerQr").addEventListener("click", function () { sizeQr(50); });
  id("readLocalCamera").addEventListener("click", cameraToggle);
  id("receiveOpenFolder").addEventListener("click", function () { openWhenDone = !openWhenDone; id("receiveOpenFolder").setAttribute("aria-checked", openWhenDone ? "true" : "false"); id("receiveOpenFolder").classList.toggle("is-on", openWhenDone); });
  id("showRemoteQr").addEventListener("click", function () { remote("showQr"); });
  id("readRemoteCamera").addEventListener("click", function () { remote("readCamera"); });
  id("qrDisplaySize").addEventListener("input", function () { text("qrSizeSetting", id("qrDisplaySize").value + " px"); });
  document.addEventListener("keydown", function (event) { if (event.key === "Escape") { if (qrToken) stopQr(); id("toast").hidden = true; } });
  id("toast").addEventListener("click", function () { id("toast").hidden = true; });
  id("cameraHelpDialog").hidden = true;
  id("appShell").setAttribute("data-role", "local");
  text("statusCamera", "未接続"); id("statusCamera").classList.add("is-muted"); text("cameraLabel", "カメラは未接続");
  text("pairingStatus", "スマホ連携 URL を確認中");
  request("/api/status", null, function (status, error) {
    if (error) { message(error); return; }
    text("deviceName", status.device);
    choosePage("optical");
    refresh("optical");
  });
  request("/api/remote-entry", null, function (result, error) {
    text("pairingStatus", error || "スマホでこの URL を開いてください");
    if (!error) { text("pairingUrl", result.url); id("pairingUrl").hidden = false;
      try { nativeCall({ native: "pairing", url: result.url }); id("pairingQr").hidden = false; } catch (e) { message(e.message); }
    }
  });
  var pollingRemotes = false;
  setInterval(function () {
    if (pollingRemotes) return;
    pollingRemotes = true;
    request("/api/remotes", null, function (result, error) {
      pollingRemotes = false;
      if (error) { remoteCount = 0; text("connectedRemotes", error); }
      else { remoteCount = result.remotes.length; text("connectedRemotes", result.remotes.map(function (r) { return r.name; }).join(" / ")); }
      updateActions();
    });
  }, 2000);
  window.addEventListener("resize", function () { if (qrToken) sizeQr(0); });
  choosePage("optical"); updateActions();
}());
