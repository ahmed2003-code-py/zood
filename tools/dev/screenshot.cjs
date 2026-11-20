const { chromium } = require(process.argv[2]);
(async () => {
  const b = await chromium.launch();
  const p = await b.newPage({ viewport: { width: 1440, height: 1000 } });
  await p.goto('file:///' + process.argv[3]);
  await p.waitForTimeout(1500);
  await p.screenshot({ path: process.argv[4], fullPage: true });
  await b.close();
})();
