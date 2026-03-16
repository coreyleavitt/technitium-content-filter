// #75: Uses shared initDomainListEditor from common.js
const editor = initDomainListEditor({
    textareaId: 'rulesText',
    countId: 'ruleCount',
    countLabel: 'rule',
    profileKey: 'customRules',
    payloadKey: 'rules',
    apiPath: API_PATHS.rules,
    filterComments: true,
});

// Expose save for onclick handler in template
function saveRules() {
    editor.save();
}
