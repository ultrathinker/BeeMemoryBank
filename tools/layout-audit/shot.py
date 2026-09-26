# Walks every page of the web UI at several window sizes, saves a screenshot per page and size,
# and measures what the eye would catch: page-level horizontal scroll, elements running past the
# window edge, clipped buttons/inputs/text, and header items wrapping onto extra lines.
#
#   python shot.py <outdir> --password <admin password> [--base http://127.0.0.1:5310] [--pages Tree,Search] [--sizes 360x740,800x600]
#
# Needs `pip install playwright` and Microsoft Edge (used as the browser, nothing is downloaded).
import argparse, json, os, sys, time
from playwright.sync_api import sync_playwright

ap = argparse.ArgumentParser()
ap.add_argument("out")
ap.add_argument("--base", default="http://127.0.0.1:5310")
ap.add_argument("--user", default="admin")
ap.add_argument("--password", default=os.environ.get("BMB_AUDIT_PASSWORD", ""), help="or set BMB_AUDIT_PASSWORD")
ap.add_argument("--pages", default="")
ap.add_argument("--sizes", default="360x740,480x800,640x720,800x600,1024x700,1366x768")
args = ap.parse_args()
os.makedirs(args.out, exist_ok=True)
sizes = [tuple(int(v) for v in s.split("x")) for s in args.sizes.split(",")]

MEASURE = r"""
() => {
  const vw = document.documentElement.clientWidth, vh = window.innerHeight;
  const out = { pageScrollX: document.documentElement.scrollWidth - vw, offRight: [], clipped: [], wrapped: [] };
  const name = el => {
    let s = el.tagName.toLowerCase();
    if (el.id) s += '#' + el.id;
    if (typeof el.className === 'string' && el.className.trim()) s += '.' + el.className.trim().split(/\s+/).slice(0, 3).join('.');
    const t = (el.innerText || el.getAttribute('aria-label') || el.getAttribute('label') || '').trim().replace(/\s+/g, ' ').slice(0, 40);
    return t ? `${s} "${t}"` : s;
  };
  const visible = el => {
    const cs = getComputedStyle(el);
    if (cs.visibility === 'hidden' || cs.display === 'none' || +cs.opacity === 0) return false;
    const r = el.getBoundingClientRect();
    return r.width > 0 && r.height > 0;
  };
  // Is the element inside something that scrolls horizontally on purpose (tables, code)?
  const inHScroller = el => {
    for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
      const ox = getComputedStyle(p).overflowX;
      if ((ox === 'auto' || ox === 'scroll') && p.scrollWidth > p.clientWidth + 1) return true;
    }
    return false;
  };
  const interesting = 'a,button,sl-button,sl-icon-button,input,sl-input,select,sl-select,textarea,h1,h2,h3,h4,label,.btn,[role=button],img,svg,sl-tab,nav *';
  for (const el of document.querySelectorAll(interesting)) {
    if (!visible(el)) continue;
    const r = el.getBoundingClientRect();
    if ((r.right > vw + 1 || r.left < -1) && !inHScroller(el)) out.offRight.push(`${name(el)} [${Math.round(r.left)}..${Math.round(r.right)} of ${vw}]`);
  }
  // Clipped: a box that hides overflow while its content is wider/taller than it.
  for (const el of document.querySelectorAll('body *')) {
    if (!visible(el)) continue;
    const cs = getComputedStyle(el);
    const hid = v => v === 'hidden' || v === 'clip';
    if ((hid(cs.overflowX) && el.scrollWidth > el.clientWidth + 2) || (hid(cs.overflowY) && el.scrollHeight > el.clientHeight + 2 && cs.textOverflow !== 'ellipsis' && cs.webkitLineClamp === 'none')) {
      if (cs.textOverflow === 'ellipsis') continue; // deliberate truncation
      out.clipped.push(`${name(el)} [scroll ${el.scrollWidth}x${el.scrollHeight} box ${el.clientWidth}x${el.clientHeight}]`);
    }
  }
  // Short labels that broke onto several lines (logo, nav items, buttons).
  for (const el of document.querySelectorAll('header *, nav *, .navbar *, button, sl-button, .brand, .logo, a')) {
    if (!visible(el) || el.children.length > 3) continue;
    const t = (el.innerText || '').trim();
    if (!t || t.length > 40) continue;
    const lh = parseFloat(getComputedStyle(el).lineHeight) || 20;
    const r = el.getBoundingClientRect();
    if (r.height > lh * 1.8 && r.height < 200) out.wrapped.push(`${name(el)} h=${Math.round(r.height)}`);
  }
  for (const k of ['offRight', 'clipped', 'wrapped']) out[k] = [...new Set(out[k])].slice(0, 40);
  return out;
}
"""

def discover(page):
    """An article id and a folder that exists on this node (the longest article, for a real page)."""
    root = page.evaluate("async () => (await (await fetch('/api-proxy/tree/children?path=/')).json())")
    arts = root.get("articles") or []
    folders = root.get("folders") or []
    art = arts[0]["id"] if arts else None
    folder = folders[0]["path"] if folders else None
    return art, folder, root

with sync_playwright() as p:
    browser = p.chromium.launch(channel="msedge", headless=True)
    ctx = browser.new_context(viewport={"width": 1280, "height": 800})
    page = ctx.new_page()
    page.goto(args.base + "/Login")
    page.fill("sl-input[name=username] input", args.user)
    page.fill("sl-input[name=password] input", args.password)
    page.keyboard.press("Enter")
    page.wait_for_url("**/Tree**", timeout=20000)
    art, folder, root = discover(page)
    json.dump(root, open(os.path.join(args.out, "_tree-root.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print("article", art, "folder", folder, flush=True)

    pages = {
        "Tree": "/Tree",
        "ArticleView": f"/Article/View?id={art}",
        "ArticleEdit": f"/Article/Edit?id={art}",
        "ArticleNew": "/Article/Edit?treePath=%2F",
        "ArticleHistory": f"/Article/History?id={art}",
        "Folder": f"/Folder?path={folder or '/'}",
        "Search": "/Search?q=the",
        "Graph": "/Graph",
        "Tags": "/Tags",
        "Profile": "/Profile",
        "Users": "/Users",
        "Roles": "/Roles",
        "Activity": "/Activity",
        "Admin": "/Admin",
        "InternetAccess": "/InternetAccess",
        "RemoteAccounts": "/RemoteAccounts",
        "Connect": "/Connect",
        "AI": "/AI",
    }
    if args.pages:
        keep = set(args.pages.split(","))
        pages = {k: v for k, v in pages.items() if k in keep}

    report = {}
    total = len(pages) * len(sizes); n = 0; t0 = time.time()
    for key, url in pages.items():
        for (w, h) in sizes:
            n += 1
            page.set_viewport_size({"width": w, "height": h})
            try:
                page.goto(args.base + url, wait_until="networkidle", timeout=30000)
            except Exception as e:
                print(f"[{n}/{total}] {key} {w}x{h} load: {e.__class__.__name__}", flush=True)
            time.sleep(0.8)
            shot = os.path.join(args.out, f"{key}-{w}x{h}.png")
            page.screenshot(path=shot)
            m = page.evaluate(MEASURE)
            m["url"] = page.url
            report[f"{key}-{w}x{h}"] = m
            flags = len(m["offRight"]) + len(m["clipped"]) + len(m["wrapped"]) + (1 if m["pageScrollX"] > 0 else 0)
            eta = (time.time() - t0) / n * (total - n)
            print(f"[{n}/{total}] {key} {w}x{h} issues={flags} scrollX={m['pageScrollX']} eta={eta:.0f}s", flush=True)

    # Interactive states on a phone-sized window: the tree drawer and the header menu open.
    page.set_viewport_size({"width": 360, "height": 740})
    page.goto(args.base + "/Tree", wait_until="networkidle"); time.sleep(0.8)
    page.click("#btn-header-tree"); time.sleep(0.8)
    page.screenshot(path=os.path.join(args.out, "TreeDrawer-360x740.png"))
    page.goto(args.base + f"/Article/View?id={art}", wait_until="networkidle"); time.sleep(0.8)
    page.click(".header-menu-button"); time.sleep(0.8)
    page.screenshot(path=os.path.join(args.out, "HeaderMenu-360x740.png"))
    print("interactive shots done", flush=True)

    # Logged-out pages last.
    ctx2 = browser.new_context()
    p2 = ctx2.new_page()
    for (w, h) in sizes:
        p2.set_viewport_size({"width": w, "height": h})
        p2.goto(args.base + "/Login", wait_until="networkidle")
        p2.screenshot(path=os.path.join(args.out, f"Login-{w}x{h}.png"))
        report[f"Login-{w}x{h}"] = p2.evaluate(MEASURE)
    json.dump(report, open(os.path.join(args.out, "_report.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print("DONE", len(report), "shots", flush=True)
    browser.close()
