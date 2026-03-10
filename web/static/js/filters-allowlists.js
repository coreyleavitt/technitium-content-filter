// #75: Uses shared initDomainListEditor from common.js
const editor = initDomainListEditor({
    textareaId: 'allowListText',
    countId: 'domainCount',
    countLabel: 'domain',
    profileKey: 'allowList',
    payloadKey: 'domains',
    apiPath: API_PATHS.allowlists,
    filterComments: false,
});

// Expose save for onclick handler in template
function saveAllowList() {
    editor.save();
}
