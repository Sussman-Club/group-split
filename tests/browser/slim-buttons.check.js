// Real local app styles; long copy is changed only in this browser's DOM, never saved.
async page => {
    const origin=new URL(page.url()).origin;
    if(!origin.startsWith('https://localhost:')) throw new Error('Local frontend only');
    await page.emulateMedia({reducedMotion:'reduce'});
    const results=[];
    for(const size of [{width:320,height:568},{width:375,height:812},{width:430,height:932},{width:844,height:390}]) {
        await page.setViewportSize(size);
        await page.goto(origin+'/account');
        await page.locator('html[data-gs-ready]').waitFor();
        await page.waitForLoadState('networkidle');
        const download=page.getByRole('button',{name:'Download my records and receipt files (ZIP)',exact:true});
        await download.scrollIntoViewIfNeeded();
        const normal=await download.evaluate(button=>{
            const rect=button.getBoundingClientRect();
            const hit=document.elementFromPoint(rect.left+rect.width/2,rect.top-5);
            return {height:rect.height,padding:getComputedStyle(button).paddingTop,expandedHit:hit===button||button.contains(hit)};
        });
        if(Math.abs(normal.height-32)>.5 || normal.padding!=='6px' || !normal.expandedHit)
            throw new Error('Slim geometry: '+JSON.stringify({size,...normal}));
        for(const text of [
            'Download all my records and original receipt files as a ZIP archive',
            'DownloadRecordsAndOriginalReceiptFilesFromEverySharedGroupWithoutLosingAnyDetails',
            'Preparing your records and original receipt files for download…'
        ]) {
            await download.evaluate((button,text)=>{
                const label=button.querySelector('.mud-button-label');
                const node=[...label.childNodes].find(n=>n.nodeType===Node.TEXT_NODE && n.textContent.trim());
                if(!node) throw new Error('No action label text');
                node.textContent=text;
            },text);
            const layout=await download.evaluate(button=>{
                const rect=button.getBoundingClientRect(), card=button.closest('.gs-card').getBoundingClientRect();
                const label=button.querySelector('.mud-button-label');
                const icon=button.querySelector('.mud-button-icon-start').getBoundingClientRect();
                return {height:rect.height,clipped:label.scrollWidth>label.clientWidth+1,
                    overflow:rect.right>card.right || rect.left<card.left || document.documentElement.scrollWidth>innerWidth+1,
                    iconInside:icon.left>=rect.left && icon.right<=rect.right && icon.top>=rect.top && icon.bottom<=rect.bottom};
            });
            if(layout.height<31.5 || (size.width===320 && layout.height<=32) || layout.clipped || layout.overflow || !layout.iconInside)
                throw new Error('Long action label: '+JSON.stringify({size,text,...layout}));
            await download.click({trial:true});
            results.push({...size,text,...layout});
            if(size.width===375 && text.startsWith('Download all')) {
                await page.locator('.gs-card').filter({has:page.getByRole('heading',{name:'Your records',exact:true})})
                    .screenshot({path:'artifacts/buttons-slim-long-label.png'});
            }
        }
        const manage=page.locator('.gs-account-action:has(.mud-button-icon-end)').first();
        await manage.evaluate(button=>{
            const label=button.querySelector('.mud-button-label');
            [...label.childNodes].find(n=>n.nodeType===Node.TEXT_NODE && n.textContent.trim()).textContent=
                'Manage linked accounts, passwords, and security settings';
        });
        const paired=await manage.evaluate(button=>{
            const rect=button.getBoundingClientRect();
            const icons=[...button.querySelectorAll('.mud-button-icon-start,.mud-button-icon-end')];
            return {height:rect.height,iconsInside:icons.length===2 && icons.every(icon=>{
                const box=icon.getBoundingClientRect();return box.left>=rect.left && box.right<=rect.right;
            }),clipped:button.scrollWidth>button.clientWidth+1};
        });
        if(!paired.iconsInside || paired.clipped) throw new Error('Long label between icons: '+JSON.stringify({size,...paired}));
        results.push({...size,text:'Long label with start and end icons',...paired});
    }
    return {longLabelScenarios:results.length,expandedHitAreas:true,results};
}
