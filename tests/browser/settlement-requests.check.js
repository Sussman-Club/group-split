// Run in a signed-in local requester session with playwright-cli run-code --filename=...
// The recipient session must be saved at .playwright-cli/recipient447-state.json.
// Uses two disposable local groups and real API/database calls, without route mocks.
async page => {
    const origin = new URL(page.url()).origin;
    if (!origin.startsWith('https://localhost:')) throw new Error('Local frontend only');
    const recipientContext = await page.context().browser().newContext({
        ignoreHTTPSErrors: true, storageState: '.playwright-cli/recipient447-state.json'
    });
    const recipient = await recipientContext.newPage();
    const check = (condition, message) => { if (!condition) throw new Error(message); };
    const call = async (context, path, method = 'GET', data) => {
        const response = await context.request.fetch(origin + '/api' + path, {
            method, data, ignoreHTTPSErrors:true, headers: {'X-GroupSplit-Request':'1'}
        });
        if (!response.ok()) throw new Error(`${method} ${path}: ${response.status()} ${await response.text()}`);
        const body = await response.text();
        return body ? JSON.parse(body) : null;
    };
    const requester = page.context();
    // Clear only prior runs' disposable group ledgers so the check is repeatable.
    for (const group of (await call(requester, '/groups')).filter(group=>group.name.startsWith('Request browser '))) {
        const entries = await call(requester, `/groups/${group.id}/activity?PageSize=100`);
        for (const entry of entries.items) await call(requester, `/transactions/${entry.id}`, 'DELETE');
    }
    const other = await call(recipientContext, '/users/me');
    const groups = [];
    const suffix = Date.now();
    for (const [index, amount] of [100,60].entries()) {
        const group = await call(requester, '/groups', 'POST', {name:`Request browser ${suffix} ${index}`,currency:'USD'});
        groups.push(group);
        const link = await call(requester, `/groups/${group.id}/join-links`, 'POST');
        await call(recipientContext, `/invitations/links/${link.token}/accept`, 'POST');
        await call(requester, '/transactions', 'POST', {groupId:group.id,name:'Browser test dinner',amount,
            dateTime:'2026-08-20T12:00:00Z'});
    }
    await call(requester, '/transactions', 'POST', {groupId:groups[0].id,name:'Later groceries',amount:80,
        paidByUserId:other.id,dateTime:'2026-09-10T12:00:00Z'});
    const balance = async group => call(requester, `/groups/${group.id}/balances`);
    const createViaUi = async (group, amount) => {
        await page.goto(origin + `/groups/${group.id}`);
        await page.getByRole('button',{name:'Request payment',exact:true}).first().click();
        await page.getByRole('spinbutton',{name:'Amount to request'}).fill(String(amount));
        const creating = page.waitForResponse(response => response.request().method() === 'POST' &&
            new URL(response.url()).pathname.endsWith('/settlement-requests'));
        await page.getByRole('button',{name:'Create link',exact:true}).click();
        const response = await creating;
        check(response.ok(), 'UI request creation failed: ' + await response.text());
        const request = await response.json();
        await page.getByRole('textbox',{name:'Settlement request link'}).waitFor();
        return request;
    };
    // Group entry point, form validation, and cancellation.
    await page.goto(origin + `/groups/${groups[0].id}`);
    await page.getByRole('button',{name:'Request payment',exact:true}).first().click();
    const amount = page.getByRole('spinbutton',{name:'Amount to request'});
    await amount.fill('0'); await amount.press('Tab');
    check(await page.getByRole('button',{name:'Create link',exact:true}).isDisabled(),'Zero should be refused');
    await page.getByRole('button',{name:'Cancel',exact:true}).click();
    const cancelled = await createViaUi(groups[0], 10);
    await page.getByRole('button',{name:'Cancel request',exact:true}).click();
    await page.getByRole('dialog').waitFor({state:'hidden'});
    await recipient.goto(`${origin}/settlement-requests/${cancelled.token}`);
    await recipient.getByText('This request was cancelled.',{exact:true}).waitFor();
    // A changed balance is stale, even if it is changed back later.
    const stale = await createViaUi(groups[0], 10);
    await page.getByRole('button',{name:'Done',exact:true}).click();
    const extra = await call(requester, '/transactions','POST',{groupId:groups[0].id,name:'Balance change',amount:2,
        dateTime:new Date().toISOString()});
    await recipient.goto(`${origin}/settlement-requests/${stale.token}`);
    await recipient.getByText(/This request is stale because the balance changed/).waitFor();
    check(await recipient.getByRole('button',{name:'Record repayment',exact:true}).count() === 0,'Stale request is actionable');
    await call(requester, `/transactions/${extra.id}`,'DELETE');
    // Requests are group-only; the all-groups screen no longer offers one.
    await page.goto(origin + '/settle');
    await page.getByRole('heading',{name:'Settle up',exact:true}).waitFor();
    check(await page.getByRole('button',{name:'Request payment',exact:true}).count() === 0,'Cross-group request still visible');
    // Selecting August suggests $50 even though today's group debt is only $10.
    const before = await Promise.all(groups.map(balance));
    await page.goto(origin + `/groups/${groups[0].id}`);
    await page.getByRole('button',{name:'Request payment',exact:true}).click();
    await page.getByRole('textbox',{name:'Up to a date',exact:true}).click();
    for (let count = 0; await page.getByRole('button',{name:/Previous month/}).getAttribute('aria-label') !== 'Previous month (July 2026)'; count++) {
        check(count < 36, 'Could not reach August 2026 in the calendar');
        await page.getByRole('button',{name:/Previous month/}).click();
    }
    await page.getByRole('button').filter({hasText:/^31$/}).filter({visible:true}).last().click();
    const periodAmount = page.getByRole('spinbutton',{name:'Amount to request'});
    await periodAmount.waitFor();
    await page.waitForFunction(()=>[...document.querySelectorAll('input')].some(input=>Number(input.value)===50));
    check(Number(await periodAmount.inputValue()) === 50,'Earlier-period amount should be 50');
    const creating = page.waitForResponse(response=>response.request().method()==='POST' &&
        new URL(response.url()).pathname.endsWith('/settlement-requests'));
    await page.getByRole('button',{name:'Create link',exact:true}).click();
    const requested = await (await creating).json();
    check(requested.groups.length === 1 && requested.groups[0].groupId === groups[0].id,'Request must cover only this group');
    check(requested.through === '2026-08-31','Cutoff was not saved');
    check(requested.amount === 50,'Period amount was not saved');
    await page.getByRole('button',{name:'Copy request link',exact:true}).click();
    await page.getByRole('button',{name:'Done',exact:true}).click();
    const afterCreation = await Promise.all(groups.map(balance));
    check(JSON.stringify(before) === JSON.stringify(afterCreation),'Creating request moved a balance');
    // The creator is not the named recipient.
    await page.goto(`${origin}/settlement-requests/${requested.token}`);
    await page.getByText('This request is unavailable',{exact:true}).waitFor();
    check(await page.getByRole('button',{name:'Record repayment',exact:true}).count() === 0,'Wrong account can record');
    // Mobile layout and explicit payment confirmation.
    await recipient.goto(`${origin}/settlement-requests/${requested.token}`);
    const record = recipient.getByRole('button',{name:'Record repayment',exact:true});
    await record.waitFor();
    await recipient.getByText('For activity up to 31 Aug 2026',{exact:true}).waitFor();
    check(await record.isDisabled(),'Recording needs explicit outside-payment confirmation');
    for (const width of [360,390,768,1440]) {
        await recipient.setViewportSize({width,height:900});
        check(await recipient.evaluate(()=>document.documentElement.scrollWidth <= innerWidth),`Overflow at ${width}px`);
    }
    await recipient.setViewportSize({width:390,height:844});
    await recipient.screenshot({path:'.playwright-cli/settlement-request-mobile.png',fullPage:true});
    await recipient.getByRole('button',{name:'Copy payment message',exact:true}).click();
    await recipient.getByRole('checkbox',{name:'I have paid this amount outside GroupSplit'}).check();
    await record.click();
    await recipient.getByText('This repayment has been recorded.',{exact:true}).waitFor();
    await recipient.reload();
    await recipient.getByText('This repayment has been recorded.',{exact:true}).waitFor();
    const replay = await recipientContext.request.post(`${origin}/api/settlement-requests/${requested.token}/repayment`,
        {headers:{'X-GroupSplit-Request':'1'}});
    check(replay.status() === 409,'Repeated repayment should be refused');
    const afterPayment = await Promise.all(groups.map(balance));
    const net = rows => rows.netBalances.find(row=>row.userId===other.id).balance;
    check(net(afterPayment[0]) - net(before[0]) === 50,'Recorded payment must move the debtor balance by exactly 50');
    check(JSON.stringify(afterPayment[1]) === JSON.stringify(before[1]),'Payment changed an unrelated group');
    await recipient.screenshot({path:'.playwright-cli/settlement-request-completed.png',fullPage:true});
    await recipientContext.close();
    return {groupEntry:true,crossGroupRemoved:true,period:'2026-08-31',zeroValidation:true,cancellation:true,stale:true,
        recipientOnly:true,noBalanceChangeOnCreation:true,aboveCurrentBalanceAmount:50,coveredGroups:1,unrelatedGroupUnchanged:true,
        explicitConfirmation:true,paidPersists:true,replayRefused:true,mobileWidths:[360,390,768,1440]};
}
