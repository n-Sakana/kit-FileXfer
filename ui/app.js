(function () {
  "use strict";

  var $ = function (id) { return document.getElementById(id); };
  var MB = 1024 * 1024;
  var FRAME_BYTES = [500, 1000, 1465, 1850, 2331, 2953];
  var FRAME_RATES = [10, 15, 20, 24, 30, 60];
  var EC_LIMIT = { L: 2953, M: 2331, Q: 1663, H: 1273 };

  // ---------------------------------------------------------------- native component

  function native(op, args) {
    var request = { op: op };
    if (args) for (var key in args) request[key] = args[key];
    var reply = JSON.parse(uiHost.call(JSON.stringify(request)));
    if (reply && reply.error) throw new Error(reply.error);
    return reply;
  }

  // Work that takes time (dialogs, building a transfer) runs as a job; the page keeps drawing meanwhile.
  function job(op, args) {
    return new Promise(function (resolve, reject) {
      var started;
      try { started = native(op, args); } catch (error) { reject(error); return; }
      (function poll() {
        var reply;
        try { reply = native("job", { id: started.job }); } catch (error) { reject(error); return; }
        if (reply.pending) { setTimeout(poll, 40); return; }
        if (reply.error) reject(new Error(reply.error)); else resolve(reply.result);
      })();
    });
  }

  // ---------------------------------------------------------------- stored preferences

  // One JSON file in the page's own storage (navigator.storage.getDirectory), read once before start-up.
  var saved = {}, savedFile = null, saving = Promise.resolve();
  function loadSaved() {
    return navigator.storage.getDirectory()
      .then(function (root) { return root.getFileHandle("settings.json", { create: true }); })
      .then(function (handle) { savedFile = handle; return handle.getFile(); })
      .then(function (file) { return file.text(); })
      .then(function (text) { var value = text ? JSON.parse(text) : {}; if (value && typeof value === "object") saved = value; })
      .catch(function () { /* the defaults apply */ });
  }
  function stored(key, fallback) { return saved[key] === undefined ? fallback : saved[key]; }
  function store(key, value) {
    saved[key] = value;
    if (!savedFile) return;
    var text = JSON.stringify(saved);
    saving = saving.then(function () { return savedFile.createWritable(); })
      .then(function (writer) { return writer.write(text).then(function () { return writer.close(); }); })
      .catch(function () { /* the setting still applies to this session */ });
  }

  var settings = {};
  function setting(name, fallback) { return settings[name] === undefined ? fallback : settings[name]; }
  function save(name, value) { settings[name] = value; store("settings", settings); }

  var state = {
    view: "send", info: null, folder: null, files: [], rows: {}, picking: false, starting: false,
    sending: null, camera: null, cameras: [], remotes: 0, activation: -1, receivedPath: ""
  };

  // ---------------------------------------------------------------- formatting

  function bytes(value) {
    var n = Number(value) || 0;
    if (n < 1024) return n + " B";
    var units = ["KB", "MB", "GB"], unit = -1;
    do { n /= 1024; unit++; } while (n >= 1024 && unit < units.length - 1);
    return (n >= 100 ? Math.round(n) : Math.round(n * 10) / 10).toLocaleString("ja-JP") + " " + units[unit];
  }
  function count(n) { return Number(n || 0).toLocaleString("ja-JP"); }
  function seconds(value) {
    var s = Math.max(0, Number(value) || 0);
    if (s < 60) return Math.max(1, Math.ceil(s)) + " 秒";
    var m = Math.floor(Math.ceil(s) / 60), rest = Math.ceil(s) % 60;
    return m + " 分" + (rest ? " " + rest + " 秒" : "");
  }
  function pad(n) { return n < 10 ? "0" + n : String(n); }
  function date(value) {
    var d = new Date(value);
    if (isNaN(d.getTime())) return "";
    return (d.getMonth() + 1) + "/" + pad(d.getDate()) + " " + pad(d.getHours()) + ":" + pad(d.getMinutes());
  }

  // ---------------------------------------------------------------- toast

  var toastTimer = 0;
  function toast(message) {
    var el = $("toast");
    el.textContent = message;
    el.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { el.hidden = true; }, 4200);
  }
  function fail(error) { toast(error && error.message ? error.message : String(error)); }

  // ---------------------------------------------------------------- theme and sidebar

  function applyTheme(theme) {
    document.documentElement.setAttribute("data-theme", theme);
    $("themeButton").textContent = theme === "dark" ? "ライトにする" : "ダークにする";
  }
  $("themeButton").addEventListener("click", function () {
    var next = document.documentElement.getAttribute("data-theme") === "dark" ? "light" : "dark";
    applyTheme(next);
    store("theme", next);
  });

  var sidebar = $("sidebar"), resizer = $("resizer");
  function sidebarWidth(width) { return Math.max(180, Math.min(320, Math.round(width))); }
  var drag = null;
  resizer.addEventListener("pointerdown", function (event) {
    drag = { x: event.clientX, width: sidebar.getBoundingClientRect().width };
    resizer.setPointerCapture(event.pointerId);
    resizer.classList.add("is-dragging");
    event.preventDefault();
  });
  resizer.addEventListener("pointermove", function (event) {
    if (!drag) return;
    sidebar.style.width = sidebarWidth(drag.width + event.clientX - drag.x) + "px";
  });
  function endDrag() {
    if (!drag) return;
    drag = null;
    resizer.classList.remove("is-dragging");
    store("sidebar", sidebarWidth(sidebar.getBoundingClientRect().width));
  }
  resizer.addEventListener("pointerup", endDrag);
  resizer.addEventListener("pointercancel", endDrag);

  // ---------------------------------------------------------------- views

  document.querySelectorAll("[data-view]").forEach(function (button) {
    button.addEventListener("click", function () { showView(button.getAttribute("data-view")); });
  });

  function showView(view) {
    if (view !== "receive" && state.camera) stopReceiving();
    state.view = view;
    document.querySelectorAll("[data-view]").forEach(function (button) {
      button.classList.toggle("is-active", button.getAttribute("data-view") === view);
    });
    document.querySelectorAll("[data-panel]").forEach(function (panel) {
      panel.hidden = panel.getAttribute("data-panel") !== view;
    });
    closeMenu();
    if (view === "receive") loadCameras();
    if (view === "phone") loadPhone();
  }

  // ---------------------------------------------------------------- menus

  var openMenu = null;
  function closeMenu() {
    if (!openMenu) return;
    openMenu.element.remove();
    openMenu.owner.setAttribute("aria-expanded", "false");
    openMenu = null;
  }

  function showMenu(owner, items, choose) {
    if (openMenu && openMenu.owner === owner) { closeMenu(); return; }
    closeMenu();
    var menu = document.createElement("div");
    menu.className = "menu";
    menu.setAttribute("role", "listbox");
    items.forEach(function (item) {
      var button = document.createElement("button");
      button.type = "button";
      button.className = "menu-item" + (item.on ? " is-on" : "");
      button.setAttribute("role", "option");
      button.setAttribute("aria-selected", item.on ? "true" : "false");
      button.textContent = item.label;
      button.disabled = !!item.disabled;
      button.addEventListener("click", function (event) {
        event.stopPropagation();
        closeMenu();
        choose(item.value);
      });
      menu.appendChild(button);
    });
    var box = owner.getBoundingClientRect();
    menu.style.left = Math.round(box.left) + "px";
    menu.style.top = Math.round(box.bottom + 4) + "px";
    menu.style.minWidth = Math.round(box.width) + "px";
    $("menuLayer").appendChild(menu);
    // Keep the menu inside the window: open upwards when there is no room below.
    var height = menu.getBoundingClientRect().height;
    if (box.bottom + 4 + height > window.innerHeight - 8) menu.style.top = Math.max(8, Math.round(box.top - 4 - height)) + "px";
    owner.setAttribute("aria-expanded", "true");
    openMenu = { element: menu, owner: owner };
  }

  document.addEventListener("click", function (event) {
    if (openMenu && !openMenu.element.contains(event.target) && !openMenu.owner.contains(event.target)) closeMenu();
  });

  // ---------------------------------------------------------------- send: selection

  function loadFolder() {
    var first = native("folder", { offset: 0 });
    var files = first.files.slice();
    while (files.length < first.count) {
      var next = native("folder", { offset: files.length });
      if (!next.files.length) break;
      files = files.concat(next.files);
    }
    state.folder = first;
    state.files = files;
    renderFolder();
  }

  // A long path keeps its start and its last folders, so the chosen folder stays readable.
  function shortPath(path, element) {
    if (!path) return "";
    var room = Math.max(24, Math.floor(element.clientWidth / 7.2));
    var weight = function (value) { var n = 0; for (var i = 0; i < value.length; i++) n += value.charCodeAt(i) > 0x2e80 ? 2 : 1; return n; };
    if (weight(path) <= room) return path;
    var sep = path.indexOf("\\") >= 0 ? "\\" : "/";
    var parts = path.split(sep), head = parts[0], tail = [];
    for (var i = parts.length - 1; i > 0; i--) {
      var next = [parts[i]].concat(tail);
      if (tail.length > 0 && weight(head + sep + "…" + sep + next.join(sep)) > room) break;
      tail = next;
    }
    return head + sep + "…" + sep + tail.join(sep);
  }

  function renderPath() {
    var element = $("sourcePath"), path = state.folder ? state.folder.path || "" : "";
    element.textContent = shortPath(path, element);
    element.title = path;
  }

  function renderFolder() {
    var folder = state.folder;
    renderPath();
    var list = $("fileList");
    list.replaceChildren();
    state.rows = {};
    var fragment = document.createDocumentFragment();
    state.files.forEach(function (file) {
      var row = document.createElement("button");
      row.type = "button";
      row.className = "row" + (file.selected ? " is-on" : "");
      row.setAttribute("role", "checkbox");
      row.setAttribute("aria-checked", file.selected ? "true" : "false");
      row.title = file.name;
      var cell = document.createElement("span"); cell.className = "check-cell";
      var check = document.createElement("span"); check.className = "check";
      cell.appendChild(check);
      row.appendChild(cell);
      row.appendChild(text("c-name", file.name));
      row.appendChild(text("c-size", bytes(file.size)));
      row.appendChild(text("c-date", date(file.modified)));
      row.addEventListener("click", function () { toggleFile(file); });
      state.rows[file.name] = row;
      fragment.appendChild(row);
    });
    list.appendChild(fragment);
    list.scrollTop = 0;
    $("fileEmpty").hidden = state.files.length > 0;
    renderTotals(folder);
  }

  function text(className, value) {
    var span = document.createElement("span");
    span.className = className;
    span.textContent = value;
    return span;
  }

  function toggleFile(file) {
    try {
      var totals = native("select", { name: file.name, on: !file.selected });
      file.selected = !file.selected;
      var row = state.rows[file.name];
      row.classList.toggle("is-on", file.selected);
      row.setAttribute("aria-checked", file.selected ? "true" : "false");
      renderTotals(totals);
    } catch (error) { fail(error); }
  }

  $("selectAll").addEventListener("click", function () {
    var on = state.folder.selectedCount < state.files.length;
    try {
      var totals = native("selectAll", { on: on });
      state.files.forEach(function (file) {
        file.selected = on;
        var row = state.rows[file.name];
        row.classList.toggle("is-on", on);
        row.setAttribute("aria-checked", on ? "true" : "false");
      });
      renderTotals(totals);
    } catch (error) { fail(error); }
  });

  function renderTotals(totals) {
    state.folder.selectedCount = totals.selectedCount;
    state.folder.selectedBytes = totals.selectedBytes;
    var all = $("selectAll");
    var total = state.files.length;
    all.setAttribute("aria-checked", total > 0 && totals.selectedCount === total ? "true" : totals.selectedCount > 0 ? "mixed" : "false");
    all.disabled = total === 0;
    $("sendSummary").textContent = total === 0 ? "" : count(totals.selectedCount) + " ファイル · " + bytes(totals.selectedBytes);
    updateSendButtons();
  }

  function updateSendButtons() {
    var ready = state.folder && state.folder.selectedCount > 0 && !state.starting;
    $("showQr").disabled = !ready;
    $("phoneShowQr").disabled = !ready || state.remotes === 0;
    $("pickFiles").disabled = state.picking;
    $("pickFolder").disabled = state.picking;
  }

  function pick(op) {
    if (state.picking) return;
    state.picking = true;
    updateSendButtons();
    job(op).then(function (result) {
      if (!result.cancelled) loadFolder();
    }).catch(fail).then(function () {
      state.picking = false;
      updateSendButtons();
    });
  }
  $("pickFiles").addEventListener("click", function () { pick("pickFiles"); });
  $("pickFolder").addEventListener("click", function () { pick("pickFolder"); });

  // ---------------------------------------------------------------- send: settings

  function renderSendSettings() {
    var ec = setting("ec", "L");
    var limit = EC_LIMIT[ec];
    var frameBytes = Math.min(setting("frameBytes", 1000), limit);
    if (FRAME_BYTES.indexOf(frameBytes) < 0) frameBytes = FRAME_BYTES.filter(function (v) { return v <= limit; }).pop();
    settings.frameBytes = frameBytes;
    $("frameBytes").querySelector(".select-value").textContent = count(frameBytes) + " bytes";
    $("frameRate").querySelector(".select-value").textContent = setting("fps", 30) + " fps";
    document.querySelectorAll("#ecGroup button").forEach(function (button) {
      button.classList.toggle("is-on", button.getAttribute("data-ec") === ec);
    });
    var size = setting("qrSize", 900);
    $("qrSizeValue").textContent = count(size) + " px";
    $("qrSmaller").disabled = size <= 300;
    $("qrLarger").disabled = size >= 1200;
  }

  $("frameBytes").addEventListener("click", function () {
    var limit = EC_LIMIT[setting("ec", "L")], current = setting("frameBytes", 1000);
    showMenu(this, FRAME_BYTES.map(function (value) {
      return { value: value, label: count(value) + " bytes", on: value === current, disabled: value > limit };
    }), function (value) { save("frameBytes", value); renderSendSettings(); });
  });
  $("frameRate").addEventListener("click", function () {
    var current = setting("fps", 30);
    showMenu(this, FRAME_RATES.map(function (value) {
      return { value: value, label: value + " fps", on: value === current };
    }), function (value) { save("fps", value); renderSendSettings(); });
  });
  document.querySelectorAll("#ecGroup button").forEach(function (button) {
    button.addEventListener("click", function () { save("ec", button.getAttribute("data-ec")); renderSendSettings(); });
  });
  function changeQrSize(step) {
    save("qrSize", Math.max(300, Math.min(1200, setting("qrSize", 900) + step)));
    renderSendSettings();
    if (state.sending) sizePresentation();
  }
  $("qrSmaller").addEventListener("click", function () { changeQrSize(-50); });
  $("qrLarger").addEventListener("click", function () { changeQrSize(50); });

  // ---------------------------------------------------------------- send: QR on this screen

  $("showQr").addEventListener("click", function () {
    if (!state.folder || state.folder.selectedCount === 0 || state.starting) return;
    if (state.folder.selectedBytes > 16 * MB) {
      var blocks = Math.ceil(state.folder.selectedBytes / (setting("frameBytes", 1000) - 20));
      confirm("大きいファイルを送ります", bytes(state.folder.selectedBytes) + " あります。送り終えるまで少なくとも " + seconds(blocks / setting("fps", 30)) + "かかります。", startSending);
      return;
    }
    startSending();
  });

  function startSending() {
    state.starting = true;
    updateSendButtons();
    job("send.start", { frameBytes: setting("frameBytes", 1000), fps: setting("fps", 30), ec: setting("ec", "L") }).then(function (session) {
      present(session);
    }).catch(fail).then(function () {
      state.starting = false;
      updateSendButtons();
    });
  }

  function present(session) {
    state.sending = { session: session, sequence: 0, next: 0, interval: 1000 / session.framesPerSecond, scale: 1 };
    $("present").hidden = false;
    sizePresentation();
    requestAnimationFrame(tick);
    $("stopQr").focus();
  }

  function showFrame() {
    var s = state.sending;
    $("presentImage").src = "app:qr?t=" + s.session.token + "&s=" + s.sequence + "&m=" + s.scale;
  }

  function tick(now) {
    var s = state.sending;
    if (!s) return;
    requestAnimationFrame(tick);
    if (s.next === 0) s.next = now + s.interval;
    if (now < s.next) return;
    s.sequence++;
    showFrame();
    s.next += s.interval;
    if (s.next <= now) s.next = now + s.interval; // never flash catch-up frames after a stall
  }

  // The QR image has whole device pixels per module and is placed on whole device pixels, so it is drawn one
  // image pixel per screen pixel: every module keeps the same sharp size.
  function sizePresentation() {
    var s = state.sending;
    if (!s) return;
    var side = s.session.side, ratio = window.devicePixelRatio || 1;
    var budget = Math.min(setting("qrSize", 900), window.innerWidth - 48, window.innerHeight - 110);
    s.scale = Math.max(1, Math.floor(budget * ratio / side));
    var device = side * s.scale, css = device / ratio, gap = 18, bar = 34;
    var left = Math.floor((window.innerWidth * ratio - device) / 2) / ratio;
    var top = Math.max(0, Math.floor((window.innerHeight * ratio - device - (gap + bar) * ratio) / 2) / ratio);
    var frame = $("presentFrame"), image = $("presentImage");
    frame.style.left = left + "px";
    frame.style.top = top + "px";
    image.style.width = css + "px";
    image.style.height = css + "px";
    $("presentBar").style.top = (top + css + gap) + "px";
    showFrame();
    $("presentSize").textContent = count(setting("qrSize", 900)) + " px";
    $("presentSmaller").disabled = setting("qrSize", 900) <= 300;
    $("presentLarger").disabled = setting("qrSize", 900) >= 1200;
  }
  $("presentSmaller").addEventListener("click", function () { changeQrSize(-50); });
  $("presentLarger").addEventListener("click", function () { changeQrSize(50); });

  function stopSending() {
    if (!state.sending) return;
    state.sending = null;
    $("present").hidden = true;
    try { native("send.stop"); } catch (error) { /* the session ends with the window anyway */ }
    $("showQr").focus();
  }
  $("stopQr").addEventListener("click", stopSending);
  window.addEventListener("resize", function () { sizePresentation(); sizePreview(); closeMenu(); renderPath(); });

  // ---------------------------------------------------------------- confirm dialog

  var confirmed = null;
  function confirm(title, message, then) {
    $("confirmTitle").textContent = title;
    $("confirmText").textContent = message;
    confirmed = then;
    $("confirmDialog").hidden = false;
    $("confirmOk").focus();
  }
  function closeConfirm() { $("confirmDialog").hidden = true; confirmed = null; }
  $("confirmCancel").addEventListener("click", closeConfirm);
  $("confirmOk").addEventListener("click", function () { var then = confirmed; closeConfirm(); if (then) then(); });

  // ---------------------------------------------------------------- receive

  function loadCameras() {
    try { state.cameras = native("cameras").cameras; } catch (error) { state.cameras = []; fail(error); }
    var chosen = setting("camera", "");
    if (!state.cameras.some(function (c) { return c.id === chosen; })) chosen = state.cameras.length ? state.cameras[0].id : "";
    settings.camera = chosen;
    renderReceiveSettings();
  }

  function cameraName(id) {
    for (var i = 0; i < state.cameras.length; i++) if (state.cameras[i].id === id) return state.cameras[i].name;
    return "";
  }

  function renderReceiveSettings() {
    $("cameraField").hidden = state.cameras.length < 2;
    $("cameraSelect").querySelector(".select-value").textContent = cameraName(setting("camera", ""));
    var running = !!state.camera;
    document.querySelectorAll("#widthGroup button").forEach(function (b) { b.classList.toggle("is-on", Number(b.getAttribute("data-width")) === setting("cameraWidth", 1280)); b.disabled = running; });
    document.querySelectorAll("#fpsGroup button").forEach(function (b) { b.classList.toggle("is-on", Number(b.getAttribute("data-fps")) === setting("cameraFps", 30)); b.disabled = running; });
    document.querySelectorAll("#workersGroup button").forEach(function (b) { b.classList.toggle("is-on", Number(b.getAttribute("data-workers")) === setting("workers", 2)); b.disabled = running; });
    $("cameraSelect").disabled = running;
    $("openWhenDone").setAttribute("aria-checked", setting("openWhenDone", true) ? "true" : "false");
    $("cameraButton").textContent = running ? "読むのをやめる" : "この PC のカメラで読む";
    $("cameraButton").disabled = !running && state.cameras.length === 0;
    $("phoneReadCamera").disabled = state.remotes === 0;
  }

  $("cameraSelect").addEventListener("click", function () {
    var current = setting("camera", "");
    showMenu(this, state.cameras.map(function (c) { return { value: c.id, label: c.name, on: c.id === current }; }), function (value) {
      save("camera", value); renderReceiveSettings();
    });
  });
  document.querySelectorAll("#widthGroup button").forEach(function (b) { b.addEventListener("click", function () { save("cameraWidth", Number(b.getAttribute("data-width"))); renderReceiveSettings(); }); });
  document.querySelectorAll("#fpsGroup button").forEach(function (b) { b.addEventListener("click", function () { save("cameraFps", Number(b.getAttribute("data-fps"))); renderReceiveSettings(); }); });
  document.querySelectorAll("#workersGroup button").forEach(function (b) { b.addEventListener("click", function () { save("workers", Number(b.getAttribute("data-workers"))); renderReceiveSettings(); }); });
  $("openWhenDone").addEventListener("click", function () { save("openWhenDone", !setting("openWhenDone", true)); renderReceiveSettings(); });

  $("cameraButton").addEventListener("click", function () {
    if (state.camera) stopReceiving(); else startReceiving();
  });

  function resetMeter() {
    $("receiveState").textContent = "待っています";
    $("receiveEta").textContent = "";
    $("receiveBlocks").textContent = "";
    $("receiveRate").textContent = "";
    $("receiveBar").style.width = "0%";
    $("receiveBar").classList.remove("is-done");
    $("receiveResult").hidden = true;
    $("quietHint").hidden = true;
  }

  function startReceiving() {
    resetMeter();
    try {
      native("receive.start", { camera: setting("camera", ""), width: setting("cameraWidth", 1280), fps: setting("cameraFps", 30), workers: setting("workers", 2), openWhenDone: setting("openWhenDone", true) });
    } catch (error) { fail(error); return; }
    state.camera = { preview: -1, hintDismissed: false, timer: setInterval(pollCamera, 33) };
    $("previewIdle").textContent = "";
    $("receiveState").textContent = "QR を待っています";
    renderReceiveSettings();
  }

  // The whole camera picture, as large as the preview area allows.
  function sizePreview() {
    var camera = state.camera, image = $("previewImage");
    if (!camera || !camera.width) { image.style.width = "0px"; image.style.height = "0px"; return; }
    var box = $("preview"), scale = Math.min(box.clientWidth / camera.width, box.clientHeight / camera.height);
    image.style.width = Math.floor(camera.width * scale) + "px";
    image.style.height = Math.floor(camera.height * scale) + "px";
  }

  function stopReceiving(keepMeter) {
    var camera = state.camera;
    if (!camera) return;
    clearInterval(camera.timer);
    state.camera = null;
    try { native("receive.stop"); } catch (error) { /* already stopped */ }
    $("previewImage").removeAttribute("src");
    sizePreview();
    $("previewIdle").textContent = "カメラは止まっています";
    $("quietHint").hidden = true;
    $("receiveSummary").textContent = "";
    if (!keepMeter) resetMeter();
    renderReceiveSettings();
  }

  function pollCamera() {
    var camera = state.camera;
    if (!camera) return;
    var status;
    try { status = native("receive.status"); } catch (error) { stopReceiving(true); fail(error); return; }
    if (status.previewSequence !== camera.preview && status.previewSequence > 0) {
      camera.preview = status.previewSequence;
      $("previewImage").src = "app:camera?n=" + camera.preview;
    }
    if (status.width && (status.width !== camera.width || status.height !== camera.height)) {
      camera.width = status.width;
      camera.height = status.height;
      sizePreview();
    }
    if (status.width) $("receiveSummary").textContent = status.camera + " · " + status.width + "×" + status.height + " · " + Math.round(status.fps) + " fps";
    var result = status.result;
    if (result && result.recognized) {
      var percent = Math.max(0, Math.min(100, Math.floor(result.progress * 100)));
      $("receiveBar").style.width = percent + "%";
      $("receiveBlocks").textContent = count(result.solvedBlocks) + " / " + count(result.sourceBlocks) + " ブロック";
      $("receiveRate").textContent = (Math.round(result.kilobytesPerSecond * 10) / 10).toLocaleString("ja-JP") + " KB/s";
      if (result.complete) {
        $("receiveState").textContent = "受け取りました";
        $("receiveEta").textContent = "合計 " + seconds(result.elapsedSeconds);
        $("receiveBar").classList.add("is-done");
        state.receivedPath = result.outputPath || "";
        $("receiveResultText").textContent = count(result.fileCount) + " ファイル · " + bytes(result.totalBytes) + "　" + (result.outputPath || "");
        $("receiveResult").hidden = false;
        stopReceiving(true);
        if (result.openError) toast(result.openError);
        return;
      }
      $("receiveState").textContent = "受け取っています " + percent + "%";
      var remaining = result.progress > 0 ? result.elapsedSeconds * (1 - result.progress) / result.progress : 0;
      $("receiveEta").textContent = remaining > 0 ? "残り " + seconds(remaining) : "";
    }
    if (status.error) { stopReceiving(true); $("receiveState").textContent = "止まりました"; toast(status.error); return; }
    $("quietHint").hidden = camera.hintDismissed || status.quietMs < 8000;
  }

  $("tipsButton").addEventListener("click", function () { $("tipsDialog").hidden = false; $("tipsClose").focus(); });
  $("tipsClose").addEventListener("click", function () {
    $("tipsDialog").hidden = true;
    if (state.camera) { state.camera.hintDismissed = true; $("quietHint").hidden = true; }
  });

  $("openReceived").addEventListener("click", function () { openFolder(state.receivedPath); });
  $("openOutput").addEventListener("click", function () { openFolder(state.info.outputRoot); });
  function openFolder(path) {
    try { native("openFolder", { path: path }); } catch (error) { fail(error); }
  }

  // ---------------------------------------------------------------- phone

  function loadPhone() {
    renderRemotes();
    $("pairUrl").textContent = "";
    $("pairQr").removeAttribute("src");
    $("pairQr").parentNode.classList.remove("is-ready");
    $("copyUrl").disabled = true;
    $("pairLead").textContent = "";
    job("remote.entry").then(function (entry) {
      // Whole device pixels per module, about 180 px wide.
      var image = $("pairQr"), ratio = window.devicePixelRatio || 1;
      var scale = Math.max(1, Math.floor(180 * ratio / entry.side));
      image.src = "app:pairing?m=" + scale + "&u=" + encodeURIComponent(entry.url);
      image.style.width = entry.side * scale / ratio + "px";
      image.style.height = entry.side * scale / ratio + "px";
      image.parentNode.classList.add("is-ready");
      $("pairUrl").textContent = entry.url;
      $("pairUrl").title = entry.url;
      $("copyUrl").disabled = false;
      $("pairLead").textContent = "スマホのカメラで読むと、この PC につながります。";
    }).catch(function (error) {
      $("pairLead").textContent = error.message;
    });
  }

  $("copyUrl").addEventListener("click", function () {
    var url = $("pairUrl").textContent;
    if (!url) return;
    navigator.clipboard.writeText(url).then(function () { toast("URL をコピーしました"); });
  });

  function renderRemotes() {
    var list = $("remoteList");
    var remotes = [];
    try { remotes = native("remote.list").remotes; } catch (error) { remotes = []; }
    list.replaceChildren();
    if (remotes.length === 0) { list.appendChild(text("list-empty", "つながっているスマホはありません")); return; }
    remotes.forEach(function (remote) {
      var row = document.createElement("div");
      row.className = "list-row";
      var dot = document.createElement("span"); dot.className = "list-dot";
      row.appendChild(dot);
      row.appendChild(text("", remote.name));
      list.appendChild(row);
    });
  }

  function sendToPhone(action, message) {
    try {
      native("remote.command", { action: action, frameBytes: setting("frameBytes", 1000), fps: setting("fps", 30), ec: setting("ec", "L"), displaySize: setting("qrSize", 900) });
      toast(message);
    } catch (error) { fail(error); }
  }
  $("phoneShowQr").addEventListener("click", function () { sendToPhone("showQr", "スマホに QR を出します"); });
  $("phoneReadCamera").addEventListener("click", function () { sendToPhone("readCamera", "スマホのカメラで読みます"); });

  // ---------------------------------------------------------------- keyboard and background polling

  document.addEventListener("keydown", function (event) {
    if (event.key !== "Escape") return;
    if (openMenu) { closeMenu(); return; }
    if (!$("tipsDialog").hidden) { $("tipsDialog").hidden = true; return; }
    if (!$("confirmDialog").hidden) { closeConfirm(); return; }
    if (state.sending) stopSending();
  });

  function poll() {
    var status;
    try { status = native("poll"); } catch (error) { return; }
    if (status.shutdown) { window.close(); return; }
    if (state.activation >= 0 && status.activation !== state.activation) {
      // Opened again from the context menu: show what was chosen there.
      if (state.sending) stopSending();
      try { loadFolder(); } catch (error) { fail(error); }
      showView("send");
    }
    state.activation = status.activation;
    if (status.remotes !== state.remotes) {
      state.remotes = status.remotes;
      $("phoneCount").hidden = state.remotes === 0;
      $("phoneCount").textContent = String(state.remotes);
      updateSendButtons();
      renderReceiveSettings();
      if (state.view === "phone") renderRemotes();
    }
  }

  // ---------------------------------------------------------------- start

  function start() {
    var values = stored("settings", {});
    settings = values && typeof values === "object" ? values : {};
    applyTheme(stored("theme", "dark") === "light" ? "light" : "dark");
    sidebar.style.width = sidebarWidth(stored("sidebar", 224)) + "px";
    try {
      state.info = native("init", { args: uiHost.arguments });
      $("outputPath").textContent = state.info.outputRoot;
      $("outputPath").title = state.info.outputRoot;
      loadFolder();
      loadCameras();
      if (state.info.pathError) toast(state.info.pathError);
      else if (state.info.serverError) toast("スマホとはつながりません: " + state.info.serverError);
    } catch (error) {
      fail(error);
    }
    renderSendSettings();
    renderReceiveSettings();
    poll();
    setInterval(poll, 500);
    setInterval(function () { if (state.view === "phone") renderRemotes(); }, 2000);
  }

  loadSaved().then(start);
})();
