// 도식 원본(.mmd) → PNG. 문서의 그림을 고친 뒤 다시 만들 때 쓴다 (docs/diagrams/README.md).
// 사용: node render.mjs <mermaid.min.js> <in.mmd> <out.png> [width]
//   mermaid.min.js: https://cdn.jsdelivr.net/npm/mermaid@11.4.1/dist/mermaid.min.js 를 받아 둔 경로
//   Playwright(Chromium) 필요: npm i -D playwright  (또는 PLAYWRIGHT 모듈 경로를 PW 환경변수로)
const pw = await import(process.env.PW || 'playwright');
const { chromium } = pw.default ?? pw;
import { readFileSync } from 'node:fs';
const [, , lib, inp, out, width] = process.argv;
const src = readFileSync(inp, 'utf8');
const browser = await chromium.launch(process.env.CHROMIUM ? { executablePath: process.env.CHROMIUM, args: ['--no-sandbox'] } : {});
const page = await browser.newPage({ viewport: { width: Math.max(Number(width || 1200), 2400), height: 800 }, deviceScaleFactor: 2 });
await page.setContent(`<!doctype html><html><head><meta charset="utf-8"><style>
  body{margin:0;padding:16px;background:#fff;font-family:"Malgun Gothic","Noto Sans CJK KR","Noto Sans KR",sans-serif}
  #d{display:block;width:100%}</style></head><body><div id="d"></div></body></html>`);
await page.addScriptTag({ path: lib });
const ok = await page.evaluate(async (code) => {
  mermaid.initialize({ startOnLoad: false, theme: 'base', securityLevel: 'strict',
    themeVariables: { fontFamily: '"Malgun Gothic","Noto Sans CJK KR",sans-serif', fontSize: '15px', primaryColor: '#dbeafe', primaryBorderColor: '#2563eb', primaryTextColor: '#111827', lineColor: '#4b5563', secondaryColor: '#dcfce7', tertiaryColor: '#f3f4f6', noteBkgColor: '#fef3c7', noteBorderColor: '#d97706', actorBkg: '#dbeafe', actorBorder: '#2563eb', signalColor: '#374151', labelBoxBkgColor: '#f3f4f6' },
    flowchart: { htmlLabels: true, curve: "basis", padding: 14, wrappingWidth: 320, subGraphTitleMargin: { top: 8, bottom: 8 } }, sequence: { mirrorActors: false, wrap: true, messageFontSize: 14, noteFontSize: 13 } });
  try {
    const { svg } = await mermaid.render('g', code);
    const d = document.getElementById('d'); d.innerHTML = svg;
    const el = d.querySelector('svg');
    const m = /max-width:\s*([\d.]+)px/.exec(el.getAttribute('style') || '');
    if (m) { el.style.width = m[1] + 'px'; el.style.maxWidth = 'none'; el.removeAttribute('width'); }
    return 'ok';
  }
  catch (e) { return 'ERR ' + e.message; }
}, src);
if (ok !== 'ok') { console.error(inp, ok); process.exit(1); }
await page.waitForTimeout(200);
await page.locator('#d svg').screenshot({ path: out });
console.log('rendered', out);
await browser.close();
