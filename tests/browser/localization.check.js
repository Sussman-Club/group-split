// Run in a signed-in local demo session with playwright-cli run-code --filename=tests/browser/localization.check.js.
// Only language preferences are saved; forms are opened and cancelled.
async page => {
    const origin = new URL(page.url()).origin;
    if (!origin.startsWith('https://localhost:')) throw new Error('Local frontend only');
    const response = await page.context().request.get(origin + '/api/groups', {ignoreHTTPSErrors: true});
    if (!response.ok()) throw new Error('Sign in to the local demo frontend first');
    const groups = await response.json();
    const group = groups.find(g => g.name === 'Home') ?? groups.find(g => !g.isPersonal);
    if (!group) throw new Error('A shared demo group is required');
    const results = [], errors = [];
    const recordError = error => errors.push(error.message);
    page.on('pageerror', recordError);
    const go = async path => {
        const response = await page.goto(origin + path);
        const initialLanguage = (await response.text()).match(/<html[^>]*\blang="([^"]+)"/)?.[1];
        await page.locator('html[data-gs-ready]').waitFor();
        await page.locator('.gs-loading').waitFor({state: 'hidden'});
        await page.locator('.mud-skeleton').first().waitFor({state: 'hidden'});
        await page.evaluate(() => document.fonts.ready);
        return initialLanguage;
    };
    const original = await page.context().request.get(origin + '/api/users/me', {ignoreHTTPSErrors: true}).then(r => r.json());
    const language = original.preferredLanguage ?? 'en';
    try {
        for (const [tag, settings, cancel] of [['en', 'Settings', 'Cancel'], ['es', 'Configuración', 'Cancelar'], ['pt', 'Definições', 'Cancelar']]) {
            await go('/settings');
            await page.locator('#settings-language').selectOption(tag);
            await page.waitForFunction(tag => document.documentElement.lang === tag, tag);
            await page.getByRole('heading', {name: settings, exact: true}).waitFor();
            const saved = await page.context().request.get(origin + '/api/users/me', {ignoreHTTPSErrors: true}).then(r => r.json());
            if (saved.preferredLanguage !== tag) throw new Error('Account preference did not persist: ' + tag);
            for (const width of [1280, 375]) {
                await page.setViewportSize({width, height: 900});
                for (const path of ['/', '/groups', '/inbox', '/settle', '/settings', `/groups/${group.id}`, `/groups/${group.id}/activity`, `/groups/${group.id}/settings`]) {
                    const initialLanguage = await go(path);
                    const report = await page.evaluate(() => ({
                        language: document.documentElement.lang,
                        overflow: document.documentElement.scrollWidth > innerWidth + 2,
                        title: document.title,
                        missing: /(?:Copy\.(?:Lower|Sentence)\.|MudDataGrid_|Error_[A-Z])/.test(document.body.innerText),
                        failed: !!document.querySelector('#blazor-error-ui[style*="display: block"]')
                    }));
                    if (initialLanguage !== tag || report.language !== tag || report.overflow || report.missing || report.failed)
                        throw new Error(JSON.stringify({tag, width, path, ...report}));
                    results.push({tag, width, path, title: report.title});
                    if (path === `/groups/${group.id}`) {
                        const add = page.locator('.gs-fab').first();
                        if (await add.count()) {
                            await add.click();
                            const dialog = page.getByRole('dialog');
                            await dialog.waitFor();
                            const overflow = await dialog.evaluate(el => el.scrollWidth > el.clientWidth + 2);
                            if (overflow) throw new Error(`Expense dialog overflows: ${tag}/${width}`);
                            await dialog.getByRole('button', {name: cancel, exact: true}).click();
                            await dialog.waitFor({state: 'hidden'});
                            results.push({tag, width, dialog: 'expense opened and cancelled'});
                        }
                    }
                }
            }
            // An account preference must override a stale device cookie on the next request.
            await page.context().addCookies([{name: '.AspNetCore.Culture', value: 'c=en|uic=en', url: origin}]);
            await go('/settings');
            if (await page.locator('html').getAttribute('lang') !== tag) throw new Error('Account did not override device cookie');
        }
        if (errors.length) throw new Error('Browser errors: ' + errors.join('; '));
        return {checks: results.length, errors, languages: ['en', 'es', 'pt'], widths: [1280, 375],
            dialogs: results.filter(r => r.dialog).length, accountOverridesCookie: true};
    } finally {
        page.off('pageerror', recordError);
        await go('/settings');
        await page.locator('#settings-language').selectOption(language);
        await page.waitForFunction(tag => document.documentElement.lang === tag, language);
    }
}
