// Read-only UI scenarios using local demo data. Forms are opened and cancelled, never saved.
async page => {
    const origin = new URL(page.url()).origin;
    if (!origin.startsWith('https://localhost:')) throw new Error('Local frontend only');
    const context = await page.context().browser().newContext({storageState:await page.context().storageState(),ignoreHTTPSErrors:true});
    const mobile = await context.newPage();
    await mobile.emulateMedia({reducedMotion:'reduce'});
    const response = await context.request.get(origin+'/api/groups');
    const groups = await response.json();
    const group = groups.find(g=>g.name==='Shared groceries demo') ?? groups.find(g=>!g.isPersonal);
    const results=[];
    const go = async path => {
        await mobile.goto(origin+path);
        await mobile.locator('html[data-gs-ready]').waitFor();
        await mobile.locator('.gs-loading').waitFor({state:'hidden'});
        await mobile.locator('.mud-skeleton').first().waitFor({state:'hidden'});
        await mobile.waitForLoadState('networkidle');
    };
    const check = async (name, close=true) => {
        const dialog=mobile.getByRole('dialog');
        try { await dialog.waitFor(); }
        catch(error) { throw new Error(name+JSON.stringify(await mobile.viewportSize())+': '+error.message); }
        const issue=await dialog.evaluate(element=>{
            const r=element.getBoundingClientRect();
            const buttons=[...element.querySelectorAll('.mud-dialog-actions button')];
            const rects=buttons.map(b=>b.getBoundingClientRect());
            const content=element.querySelector('.mud-dialog-content');
            return {
                overflow:r.left < -1 || r.right > innerWidth+1 || r.top < -1 || r.bottom > innerHeight+1 || element.scrollWidth>element.clientWidth+2,
                contentOverflow:content.scrollWidth>content.clientWidth+2,
                small:rects.some(r=>r.height<31.5),
                buttons:buttons.map((b,i)=>({text:b.textContent.trim(),height:rects[i].height})),
                overlap:rects.some((a,i)=>rects.slice(i+1).some(b=>Math.min(a.right,b.right)-Math.max(a.left,b.left)>1 && Math.min(a.bottom,b.bottom)-Math.max(a.top,b.top)>1))
            };
        });
        for(const action of await dialog.locator('.mud-dialog-actions button').all())
            if(await action.isEnabled()) await action.click({trial:true});
        if(issue.overflow || issue.contentOverflow || issue.small || issue.overlap) throw new Error(name+JSON.stringify(issue));
        results.push({name,...await mobile.viewportSize(),clear:true});
        if(close) {
            await dialog.getByRole('button',{name:/^(Cancel|Close|Done)$/}).first().click();
            await dialog.waitFor({state:'hidden'});
        }
    };
    try {
        for(const size of [{width:320,height:568},{width:375,height:812},{width:430,height:932},{width:640,height:360},{width:844,height:390}]) {
            await mobile.setViewportSize(size);
            await go(`/groups/${group.id}`);
            await mobile.getByRole('button',{name:'Request payment',exact:true}).click();
            await check('Request payment');
            await mobile.getByRole('button',{name:'They paid',exact:true}).click();
            await check('Record payment');
            await mobile.getByRole('button',{name:'Add expense',exact:true}).click();
            await check('Add expense',false);
            const receipt=await mobile.locator('.gs-file-picker-dropzone').evaluate(element=>{
                const browse=element.querySelector('.gs-file-picker-action').getBoundingClientRect();
                const camera=element.querySelector('.gs-file-picker-camera-action').getBoundingClientRect();
                const main=element.querySelector('.gs-file-picker-main-action').getBoundingClientRect();
                return {browseHeight:browse.height,cameraHeight:camera.height,gap:camera.top-main.bottom};
            });
            if(receipt.browseHeight<32 || receipt.cameraHeight<32 || receipt.gap<11.5)
                throw new Error('Receipt controls too close: '+JSON.stringify({size,...receipt}));
            results.push({name:'Receipt browse/camera spacing',...size,...receipt});
            for(const mode of ['Evenly:', 'One pays:', 'Custom:']) {
                await mobile.getByRole('radio',{name:new RegExp('^'+mode)}).click();
                await check('Expense '+mode,false);
            }
            await mobile.getByRole('dialog').getByRole('button',{name:'Cancel',exact:true}).click();
            await go(`/groups/${group.id}/people`);
            await mobile.getByRole('button',{name:'Add people',exact:true}).click();
            await check('Add people');
            await go(`/groups/${group.id}/settings`);
            await mobile.getByRole('button',{name:'New category',exact:true}).click();
            await check('New category');
            await mobile.getByRole('button',{name:'New rule',exact:true}).click();
            await check('New split rule',false);
            await mobile.getByRole('combobox',{name:'How does it divide?',exact:true}).click();
            await mobile.getByRole('option',{name:'Percent split',exact:true}).click();
            await check('Percentage rule');
            await go(`/groups/${group.id}`);
            const entry=mobile.locator('.gs-list .gs-row[role="button"]').first();
            if(await entry.count()) {
                await entry.click();
                await check('Expense details');
            }
        }
        return {checked:results.length,results};
    } finally { await context.close(); }
}
