// Run with playwright-cli run-code --filename=tests/browser/inbox-queue.check.js
// Requires a signed-in local WASM development frontend. All inbox writes are mocked.
async page => {
    const origin = new URL(page.url()).origin;
    if (!origin.startsWith('https://localhost:')) throw new Error('Use a local development frontend');
    const id = n => `10000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
    const home = id(90), personal = id(91), category = id(92), account = id(93);
    let rows, calls, failSecond;
    const reset = () => {
        calls = []; failSecond = false;
        rows = [1, 2, 3, 4, 5].map(n => ({
            id: id(n), date: `2026-10-${10 + n}`, amount: 20 + n, currency: 'USD',
            description: 'Grocery charge', merchantName: n === 5 ? 'New merchant' : 'Whole Foods',
            providerCategory: 'FOOD', providerCategoryDetailed: 'GROCERIES',
            authorizedDate: null, paymentChannel: 'in store', city: 'Miami', logoUrl: null,
            categoryIconUrl: null, pending: false, status: 'New', transactionId: null, removedAt: null,
            accountId: account, accountName: 'Demo card', institutionName: 'Demo bank',
            possibleDuplicates: n === 4 ? [{ transactionId: id(94), name: 'Already recorded',
                amount: 24, currency: 'USD', dateTime: '2026-10-14T12:00:00Z', groupId: home,
                groupName: 'Home', paidByUserName: 'Demo', amountDifference: 0, daysApart: 0,
                confidence: 'Confident' }] : []
        }));
    };
    reset();
    await page.route('**/api/inbox**', async route => {
        const req = route.request(), url = new URL(req.url());
        if (url.pathname === '/api/inbox/summary')
            return route.fulfill({json: {newCount: rows.filter(r => r.status === 'New').length}});
        if (url.pathname === '/api/inbox' && req.method() === 'GET') {
            const status = url.searchParams.get('Status');
            const name = status === '1' ? 'Filed' : status === '2' ? 'Ignored' : 'New';
            const items = rows.filter(r => r.status === name);
            return route.fulfill({json: {items, page: 1, pageSize: 25, totalCount: items.length}});
        }
        if (url.pathname === '/api/inbox/suggestions') {
            const wanted = req.postDataJSON().rows.map(r => r.id);
            return route.fulfill({json: rows.filter(r => wanted.includes(r.id) && [id(1),id(2),id(3)].includes(r.id))
                .map(r => ({bankTransactionId: r.id, groupId: r.id === id(3) ? personal : home,
                    groupName: r.id === id(3) ? 'Personal' : 'Home', isPersonal: r.id === id(3),
                    categoryId: category, categoryName: 'Groceries', confidence: .95}))});
        }
        if (url.pathname.endsWith('/file')) {
            const row = rows.find(r => url.pathname.includes(r.id));
            const body = req.postDataJSON(); calls.push({id: row.id, body});
            if (failSecond && row.id === id(2)) return route.fulfill({status:409,
                json:{title:'Charge changed', detail:'Review this charge again.', status:409}});
            row.status = 'Filed';
            return route.fulfill({status:201, json:{id: id(100 + calls.length), name:row.merchantName,
                amount:row.amount, currency:'USD', dateTime:'2026-10-15T12:00:00Z',
                paidByUserId:personal, paidByUserName:'Demo', splits:[]}});
        }
        return route.fallback();
    });
    const load = async () => {
        await page.goto(origin + '/inbox');
        await page.locator('.gs-inbox-bundle').waitFor();
    };
    const check = (ok, message) => { if (!ok) throw new Error(message); };
    await load();
    await page.waitForFunction(() => getComputedStyle(document.querySelector('.gs-inbox-bundle')).borderInlineStartWidth === '4px');
    check(await page.locator('.gs-inbox-bundle').count() === 1, 'Only compatible shared charges should bundle');
    check(await page.getByRole('button',{name:'File Whole Foods in Personal · Groceries',exact:true}).count() === 1,
        'Same merchant personal charge must stay separate');
    check(await page.getByText('You may already have recorded this:',{exact:true}).count() === 1,
        'Duplicate warning must remain visible');
    await page.setViewportSize({width:1440,height:1000});
    const summary = page.locator('.gs-inbox-bundle summary');
    await summary.focus(); await page.keyboard.press('Enter');
    check(await page.locator('.gs-inbox-bundle').getAttribute('open') !== null, 'Keyboard opens bundle');
    await page.screenshot({path:'.playwright-cli/inbox-queue-desktop.png',fullPage:true});
    const sizes = [{width:360,height:800},{width:390,height:844},{width:450,height:850},{width:640,height:900}];
    for (const size of sizes) {
        await page.setViewportSize(size);
        const dimensions = await page.evaluate(() => ({scroll:document.documentElement.scrollWidth,width:innerWidth}));
        check(dimensions.scroll <= dimensions.width, 'Horizontal overflow at ' + size.width);
        const actionRows = await page.locator('.gs-inbox-row-actions').evaluateAll(elements => elements.map(el => {
            const money = el.querySelector(':scope > .gs-money').getBoundingClientRect();
            const buttons = el.querySelector('.gs-inbox-row-buttons').getBoundingClientRect();
            return {x:money.x, bottom:money.bottom, buttonsTop:buttons.top};
        }));
        check(actionRows.every(row => Math.abs(row.x - actionRows[0].x) < 2),
            'Amounts must align regardless of suggestion at ' + size.width);
        check(actionRows.every(row => row.bottom <= row.buttonsTop),
            'Amounts must stay above actions at ' + size.width);
        await page.getByRole('button',{name:'Confirm 2 in Home · Groceries',exact:true}).click({trial:true});
        await page.evaluate(() => window.scrollTo(0, 0));
        await page.screenshot({path:`.playwright-cli/inbox-queue-${size.width}.png`,fullPage:true});
    }
    await page.getByRole('radio',{name:'Dark',exact:true}).check();
    await page.evaluate(() => window.scrollTo(0, 0));
    await page.screenshot({path:'.playwright-cli/inbox-queue-dark.png',fullPage:true});
    await page.getByRole('radio',{name:'Light',exact:true}).check();
    await page.getByRole('button',{name:'Likely personal (1)',exact:true}).click();
    check(await page.locator('.gs-inbox-bundle').count() === 0, 'Personal filter excludes shared bundle');
    await page.getByRole('button',{name:'Keep personal',exact:true}).click();
    await page.getByText('4 transactions left to review in this range.',{exact:true}).waitFor();
    check(calls.length === 1 && !calls[0].body.groupId, 'Personal action must not file into shared group');
    await page.getByRole('button',{name:'All charges (4)',exact:true}).click();
    await page.locator('.gs-inbox-bundle summary').click();
    await page.getByRole('button',{name:'Confirm 2 in Home · Groceries',exact:true}).click();
    await page.getByText('2 transactions left to review in this range.',{exact:true}).waitFor();
    check(calls.length === 3, 'Both displayed charges should file');
    check(calls.slice(1).every(c => c.body.groupId === home && c.body.categoryId === category && !c.body.fileAnyway),
        'Batch must retain displayed destination and duplicate checks');
    reset(); failSecond = true; await load();
    await page.locator('.gs-inbox-bundle summary').click();
    await page.getByRole('button',{name:'Confirm 2 in Home · Groceries',exact:true}).click();
    await page.getByText('1 of 2 charges filed. The remaining charges still need review.',{exact:true}).waitFor();
    check(calls.length === 2 && rows.find(r => r.id === id(2)).status === 'New', 'Failed charge must remain waiting');
    return {mixedDestinations:true,duplicateVisible:true,keyboardExpansion:true,widths:sizes.map(s=>s.width),
        personalFiling:true,batchConfirmation:true,partialFailure:true};
}
