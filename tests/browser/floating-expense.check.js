async page => {
    const results = [];
    for (const size of [{width:360,height:640},{width:640,height:360},{width:390,height:844},{width:844,height:390}]) {
        await page.setViewportSize(size);
        for (const path of ["/groups/12345678-9abc-def0-1234-56789abcdef0/activity", "/transactions"]) {
            await page.goto("https://localhost:7287" + path);
            const fab = page.getByRole("button", {name:"Add expense", exact:true});
            await fab.waitFor();
            const actions = path.startsWith("/groups") && size.width <= 640
                ? page.locator(".gs-feed-row > .mud-menu button")
                : page.locator(".gs-grid button[aria-label^='Edit']");
            const action = actions.last();
            await action.waitFor();
            await action.scrollIntoViewIfNeeded();
            // Playwright's hit test must succeed without the FAB intercepting.
            await action.click({trial:true});
            const actionBox = await action.boundingBox();
            const fabBox = await fab.boundingBox();
            const overlap = actionBox.x < fabBox.x + fabBox.width &&
                actionBox.x + actionBox.width > fabBox.x &&
                actionBox.y < fabBox.y + fabBox.height &&
                actionBox.y + actionBox.height > fabBox.y;
            if (overlap) throw new Error("FAB overlaps final action: " + path + JSON.stringify(size));
            results.push({path, ...size, lastActionClear:true});
        }
    }
    await page.setViewportSize({width:390,height:844});
    await page.goto("https://localhost:7287/groups/12345678-9abc-def0-1234-56789abcdef0/activity");
    await page.getByRole("button", {name:"Add expense", exact:true}).waitFor();
    await page.locator(".gs-feed-row").first().waitFor();
    const filters = page.getByRole("button", {name:"Search and filters", exact:true});
    await filters.click();
    await page.locator(".gs-search input").waitFor({state:"visible"});
    await filters.click();
    await page.evaluate(() => window.scrollTo(0, 400));
    await page.waitForFunction(() => document.querySelector(".gs-fab")?.classList.contains("is-scrolling"));
    await page.screenshot({path:"artifacts/floating-expense-scrolling.png"});
    await page.waitForFunction(() => !document.querySelector(".gs-fab")?.classList.contains("is-scrolling"));
    results.push({collapseOnScroll:true,expandAtRest:true});
    return results;
}
