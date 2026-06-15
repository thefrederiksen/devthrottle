/*
  Shared left navigation rail for the Cockpit's plain-HTML tool pages.

  These pages are not Blazor, so they do not get MainLayout's <NavMenu />.
  Including this script (with tool-nav.css) injects the SAME rail so the main
  menu is present on every Cockpit page. One definition, included by each tool
  page via two lines - no per-page copies.

  The item list and icons mirror Components/Layout/NavMenu.razor; keep them in
  sync if the menu changes. Links are plain anchors: navigating to a Blazor
  route does a full document load back into the app, and to another tool page
  loads that static page - both correct from a static origin.
*/
(function () {
  "use strict";

  // Menu items in NavMenu.razor order. icon = inner SVG markup (viewBox 0 0 16 16).
  var ITEMS = [
    { href: "/",            label: "Home",      icon: '<path d="M2 8.5 8 3l6 5.5M4 8v5h8V8"/>' },
    { href: "/cockpit",     label: "Cockpit",   icon: '<rect x="1.5" y="2.5" width="13" height="11" rx="1.5"/><path d="M4 6l2.5 2L4 10M8 10.5h4"/>' },
    { href: "/sessions",    label: "Sessions",  icon: '<path d="M2 4h12M2 8h12M2 12h8"/>' },
    { href: "/directors",   label: "Directors", icon: '<rect x="2" y="2.5" width="12" height="4.5" rx="1"/><rect x="2" y="9" width="12" height="4.5" rx="1"/><path d="M4.5 4.75h.01M4.5 11.25h.01"/>' },
    { href: "/fleet",       label: "Fleet",     icon: '<rect x="2" y="2" width="5" height="5" rx="1"/><rect x="9" y="2" width="5" height="5" rx="1"/><rect x="2" y="9" width="5" height="5" rx="1"/><rect x="9" y="9" width="5" height="5" rx="1"/>' },
    { href: "/feedback",    label: "Feedback",  icon: '<path d="M2.5 3.5h11v7h-6L4 13.5v-3h-1.5z"/><path d="M5 6h6M5 8h4"/>', alpha: true },
    { href: "/about",       label: "About",     icon: '<circle cx="8" cy="8" r="6.5"/><path d="M8 7.25v3.75M8 5h.01"/>' },
    { href: "/voice",       label: "Voice",     icon: '<rect x="6" y="1.5" width="4" height="7" rx="2"/><path d="M3.5 7a4.5 4.5 0 0 0 9 0M8 11.5v3M5.5 14.5h5"/>' },
    { sep: true },
    { href: "/settings",    label: "Settings",  icon: '<circle cx="8" cy="8" r="2.25"/><path d="M8 1.5v2M8 12.5v2M1.5 8h2M12.5 8h2M3.4 3.4l1.4 1.4M11.2 11.2l1.4 1.4M12.6 3.4l-1.4 1.4M4.8 11.2l-1.4 1.4"/>' },
    { href: "/exes",        label: "Builds",    icon: '<path d="M8 1.5 14 5v6l-6 3.5L2 11V5l6-3.5zM2 5l6 3.5L14 5M8 8.5v6"/>', alpha: true },
    { href: "/transcripts", label: "Recordings",icon: '<rect x="6" y="1.5" width="4" height="8" rx="2"/><path d="M3.5 7.5a4.5 4.5 0 0 0 9 0M8 12v2.5"/>', alpha: true },
    { href: "/dictionary",  label: "Dictionary",icon: '<path d="M3 2.5h8.5A1.5 1.5 0 0 1 13 4v9.5H4.5A1.5 1.5 0 0 1 3 12V2.5zM3 11.5h10M6 5.5h4"/>' },
    { href: "/keys",        label: "API Keys",  icon: '<circle cx="5" cy="8" r="2.5"/><path d="M7.5 8H14M12 8v2.5M10 8v2"/>' }
  ];

  var SVG_NS = "http://www.w3.org/2000/svg";
  var STORAGE_KEY = "cc-toolnav-collapsed";

  // Active when the current path equals the item's clean route, OR when the
  // page is served directly as /pages/<slug>.html (the dev/direct path), OR a
  // deeper route under it. Home (/) matches only the exact root.
  function isActive(href) {
    var p = location.pathname.replace(/\/+$/, "") || "/";
    if (href === "/") return p === "/";
    if (p === href || p.indexOf(href + "/") === 0) return true;
    var slug = href.slice(1);
    return p === "/pages/" + slug + ".html";
  }

  function buildRail() {
    var collapsed = localStorage.getItem(STORAGE_KEY) === "1";

    var nav = document.createElement("nav");
    nav.className = "nv" + (collapsed ? " nv-collapsed" : "");

    var head = document.createElement("div");
    head.className = "nv-head";
    var brand = document.createElement("span");
    brand.className = "nv-brand";
    brand.title = "CC Director";
    brand.textContent = "CC";
    var toggle = document.createElement("button");
    toggle.className = "nv-toggle";
    toggle.type = "button";
    toggle.innerHTML = collapsed ? "&raquo;" : "&laquo;";
    toggle.title = collapsed ? "Expand menu" : "Collapse menu";
    toggle.addEventListener("click", function () {
      var nowCollapsed = !nav.classList.contains("nv-collapsed");
      nav.classList.toggle("nv-collapsed", nowCollapsed);
      localStorage.setItem(STORAGE_KEY, nowCollapsed ? "1" : "0");
      toggle.innerHTML = nowCollapsed ? "&raquo;" : "&laquo;";
      toggle.title = nowCollapsed ? "Expand menu" : "Collapse menu";
    });
    head.appendChild(brand);
    head.appendChild(toggle);
    nav.appendChild(head);

    ITEMS.forEach(function (item) {
      if (item.alpha) return;
      if (item.sep) {
        var sep = document.createElement("div");
        sep.className = "nv-sep";
        nav.appendChild(sep);
        return;
      }
      var a = document.createElement("a");
      a.className = "nv-item" + (isActive(item.href) ? " active" : "");
      a.href = item.href;
      a.title = item.label;
      var svg = document.createElementNS(SVG_NS, "svg");
      svg.setAttribute("class", "nv-ico");
      svg.setAttribute("viewBox", "0 0 16 16");
      svg.innerHTML = item.icon;
      var label = document.createElement("span");
      label.className = "nv-label";
      label.textContent = item.label;
      a.appendChild(svg);
      a.appendChild(label);
      nav.appendChild(a);
    });

    var spacer = document.createElement("div");
    spacer.className = "nv-spacer";
    nav.appendChild(spacer);

    var foot = document.createElement("div");
    foot.className = "nv-foot";
    nav.appendChild(foot);
    // Best-effort version label to match the Blazor rail; absent on failure.
    fetch("/healthz")
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (j) {
        var v = j && (j.version || j.Version);
        if (v) { foot.textContent = "v" + String(v).split("+")[0]; foot.title = String(v); }
      })
      .catch(function () { /* footer simply stays empty */ });

    return nav;
  }

  function init() {
    if (document.querySelector(".cc-shell")) return; // idempotent

    var main = document.createElement("div");
    main.className = "cc-main";
    // Move the page's existing body content into the scrolling main pane.
    // Moving already-executed <script> nodes does not re-run them.
    while (document.body.firstChild) {
      main.appendChild(document.body.firstChild);
    }

    var shell = document.createElement("div");
    shell.className = "cc-shell";
    shell.appendChild(buildRail());
    shell.appendChild(main);
    document.body.appendChild(shell);
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
