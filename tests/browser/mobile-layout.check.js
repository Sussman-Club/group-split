// Run in a signed-in local demo session: playwright-cli run-code --filename=tests/browser/mobile-layout.check.js
async page => {
    const origin = new URL(page.url()).origin;
    if (!origin.startsWith('https://localhost:')) throw new Error('Local frontend only');
    const response = await page.context().request.get(origin + '/api/groups', {ignoreHTTPSErrors:true});
    if (!response.ok()) throw new Error('Sign in to the local demo app first');
    const groups = await response.json();
    const shared = groups.find(g => g.name === 'Shared groceries demo') ?? groups.find(g => !g.isPersonal);
    const personal = groups.find(g => g.isPersonal);
    const routes = ['/', '/groups', '/transactions', '/inbox', '/settle', '/account', '/android', '/not-found',
        ...['', '/activity', '/people', '/settings'].map(tab => `/groups/${shared.id}${tab}`),
        ...(personal ? ['', '/activity', '/settings'].map(tab => `/groups/${personal.id}${tab}`) : []),
        '/join/mobile-layout-invalid', '/claim/mobile-layout-invalid', '/settlement-requests/mobile-layout-invalid'];
    const results = [];
    await page.emulateMedia({reducedMotion:'reduce'});
    for (const width of [320, 375, 430, 768, 844]) {
        await page.setViewportSize({width, height: width === 844 ? 390 : 812});
        for (const path of routes) {
            await page.goto(origin + path);
            await page.locator('html[data-gs-ready]').waitFor();
            await page.locator('.gs-loading').waitFor({state:'hidden'});
            await page.locator('.mud-skeleton').first().waitFor({state:'hidden'});
            await page.evaluate(() => document.fonts.ready);
            const issues = await page.evaluate(() => {
                const viewport = document.documentElement.clientWidth;
                const visible = element => {
                    const rect = element.getBoundingClientRect();
                    return rect.width && rect.height && getComputedStyle(element).visibility !== 'hidden';
                };
                const escaped = [...document.querySelectorAll('main *')].filter(visible).filter(element => {
                    const rect = element.getBoundingClientRect();
                    // Offscreen descendants inside deliberate scrollers are clipped, not page overflow.
                    for (let parent=element.parentElement; parent; parent=parent.parentElement) {
                        const style=getComputedStyle(parent);
                        if (['auto','scroll','hidden','clip'].includes(style.overflowX)) return false;
                    }
                    return rect.right > viewport + 2 || rect.left < -2;
                }).slice(0, 12).map(element => ({tag:element.tagName, cls:element.className, text:element.textContent.trim().slice(0,70)}));
                const overlaps = [];
                for (const row of document.querySelectorAll('.gs-hero-row,.gs-hero-actions,.gs-plan-row,.gs-settle-row,.mud-dialog-actions')) {
                    const children = [...row.children].filter(visible);
                    for (let i=0;i<children.length;i++) for(let j=i+1;j<children.length;j++) {
                        const a=children[i].getBoundingClientRect(), b=children[j].getBoundingClientRect();
                        if (Math.min(a.right,b.right)-Math.max(a.left,b.left)>2 && Math.min(a.bottom,b.bottom)-Math.max(a.top,b.top)>2)
                            overlaps.push(row.className);
                    }
                }
                return {overflow: document.documentElement.scrollWidth > viewport + 2, escaped, overlaps};
            });
            results.push({width,path,...issues});
            if (width === 375 && ['', '/settings', '/people'].some(tab=>path===`/groups/${shared.id}${tab}`))
                await page.screenshot({path:`artifacts/mobile-${path.split('/').pop() || 'overview'}-${width}.png`,fullPage:true});
        }
    }
    const issues = results.filter(r=>r.overflow || r.escaped.length || r.overlaps.length);
    if (issues.length) throw new Error(JSON.stringify({checked:results.length,issues}));
    return {checked:results.length, issues};
}
