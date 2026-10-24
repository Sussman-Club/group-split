// Run in a signed-in local demo session. Measures buttons; never submits or downloads.
async page => {
    const origin=new URL(page.url()).origin;
    if(!origin.startsWith('https://localhost:')) throw new Error('Local frontend only');
    await page.emulateMedia({reducedMotion:'reduce'});
    const response=await page.context().request.get(origin+'/api/groups',{ignoreHTTPSErrors:true});
    const groups=await response.json();
    const group=groups.find(g=>g.name==='Shared groceries demo') ?? groups.find(g=>!g.isPersonal);
    const results=[], small=[];
    for(const size of [{width:320,height:568},{width:375,height:812},{width:430,height:932},{width:640,height:360},{width:844,height:390}]) {
        await page.setViewportSize(size);
        for(const path of ['/account','/groups','/settle','/inbox','/transactions',`/groups/${group.id}/activity`,`/groups/${group.id}/people`,`/groups/${group.id}/settings`]) {
            await page.goto(origin+path);
            await page.locator('html[data-gs-ready]').waitFor();
            await page.locator('.mud-skeleton').first().waitFor({state:'hidden'});
            await page.waitForLoadState('networkidle');
            const measured=await page.evaluate(()=>{
                const radius=getComputedStyle(document.documentElement).getPropertyValue('--gs-button-radius').trim();
                const buttons=[...document.querySelectorAll('main button,main a.mud-button-root')].filter(e=>{
                    const r=e.getBoundingClientRect(); return r.width && r.height && getComputedStyle(e).visibility!=='hidden';
                });
                return {overflow:document.documentElement.scrollWidth>innerWidth+2,
                    radius:buttons.filter(e=>e.matches('.mud-button-root') && ['borderTopLeftRadius','borderTopRightRadius','borderBottomLeftRadius','borderBottomRightRadius'].some(corner=>getComputedStyle(e)[corner]!==radius)).map(e=>({text:e.textContent.trim(),radius:getComputedStyle(e).borderRadius})),
                    small:buttons.filter(e=>e.getBoundingClientRect().height<(e.matches('.mud-button')?31.5:43.5)).map(e=>({text:e.textContent.trim(),class:e.className,height:e.getBoundingClientRect().height}))};
            });
            if(measured.overflow) throw new Error('Page overflow: '+path+JSON.stringify(size));
            if(measured.radius.length) throw new Error('Inconsistent action corners: '+JSON.stringify({path,size,buttons:measured.radius}));
            if(measured.small.length) small.push({path,size,buttons:measured.small});
            if(path==='/account') {
                const actions=page.locator('.gs-account-action');
                for(const button of await actions.all()) {
                    const box=await button.boundingBox();
                    if(box.height<31.5) throw new Error('Small account action: '+await button.innerText());
                    const gap=await button.evaluate(e=>e.getBoundingClientRect().top-e.previousElementSibling.getBoundingClientRect().bottom);
                    if(gap<15.5) throw new Error('Cramped account action: '+await button.innerText());
                    if(await button.isEnabled()) await button.click({trial:true});
                }
                if(size.width===375) {
                    await page.evaluate(()=>window.scrollTo(0,0));
                    await page.screenshot({path:'artifacts/account-buttons-mobile.png',fullPage:true});
                    await page.locator('.gs-card').filter({has:page.getByRole('heading',{name:'Your records',exact:true})}).screenshot({path:'artifacts/buttons-standard-download.png'});
                    await page.getByRole('button',{name:'Delete my account',exact:true}).screenshot({path:'artifacts/buttons-standard-delete.png'});
                }
            }
            results.push({path,...size,clear:true});
        }
    }
    if(small.length) throw new Error(JSON.stringify({checked:results.length,small}));
    return {checked:results.length,small};
}
