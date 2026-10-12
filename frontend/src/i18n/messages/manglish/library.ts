// Manglish overlay for the `library` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const library: Overlay<Messages['library']> = {
  module: {
    subtitle: 'Quick replies, call scripts and WhatsApp templates — write once only, then use everywhere.',
  },
  editor: {
    quickReplySubtitle: 'Type “/short-code” in the composer to pop it in. The variables fill themselves from the contact’s record.',
    scriptSubtitle: 'The bot reads this word for word on a one-way call — dial, read, hang up. No chit-chat one.',
    shortCodeHint: 'Only small letters, digits and dashes ah.',
    scriptNameHint: 'You pick it from the script-call button and in flow steps.',
    needShortCode: 'Give it a short code first lah — the thing agents type after “/”.',
    needScriptName: 'Give the script a name first.',
    badShortCode: 'Only small letters, digits, dashes or underscores — no spaces ah.',
    shortCodeTaken: '/{code} already got for this entity — pick another short code lah.',
    scriptNameTaken: '“{name}” already taken — choose another name can?',
    writeReply: 'Write the reply first.',
    writeScript: 'Write what the call should say first.',
    orgWideHint: 'Organization-wide templates — every entity also can use.',
    everyChannelHint: 'Shows up in every channel’s composer, even channels you add later.',
    onlyChannelsHint: 'Only shows up when replying on: {channels}.',
  },
  quickReplies: {
    intro:
      'Agents type {code} in a chat to pop one in. Share a reply across every channel, or keep it to the channels it suits. Variables like {variable} fill themselves from the contact’s record.',
    loading: 'Loading quick replies, wait ah…',
    loadFailed: 'Aiyo, quick replies cannot load — {error}',
    empty: 'No quick replies yet leh — add the answers your agents always type.',
    emptyChannel: 'That channel got no quick replies yet.',
    saved: '/{code} saved liao — type it after “/” in the composer.',
    updated: '/{code} updated, all good.',
    deleted: 'Done, /{code} deleted.',
    deleteTitle: 'Confirm delete /{code} ah?',
    deleteBody: 'It’ll vanish from every composer it was showing in.',
  },
  scripts: {
    intro:
      'The AI voice reads a call script {verbatim} on a one-way call — dial, read, hang up, settle. Use one from the phone icon next to a chat, or in a flow’s {step} step.',
    loading: 'Hang on, loading call scripts…',
    loadFailed: 'Call scripts cannot load — {error}. If it’s a network error, means the API not running yet ({command}).',
    empty: 'No call scripts yet — add the announcements you want your one-way calls to read out.',
    saved: 'Okay, “{name}” saved — pick it from the script-call button or a flow’s Script call step.',
    updated: '“{name}” updated liao.',
    deleted: '“{name}” deleted already.',
    deleteTitle: 'Really want to delete “{name}”?',
    deleteBody: 'Any flow step still using it will just skip the call.',
  },
  wa: {
    intro:
      'Outside the 24-hour window, Meta-approved templates are the only way to message a WhatsApp contact. Each one belongs to a WhatsApp Business account: live ones must pass Meta review first, sandbox ones can use straight away. Bind {slot} slots to contact fields so every send fills itself in.',
    noAccount: 'No WhatsApp Business account yet, so showing you the sandbox set first.',
    empty: 'No templates yet leh.',
    emptyLive: 'No templates yet — new ones need to pass Meta review first.',
    subtitleSandbox: 'Sandbox — kept locally, can use straight away.',
    subtitleLive: 'Goes to Meta for review first before you can use it.',
    nameHint: 'Small letters, digits and underscores only.',
    bodyHint: 'Use {{1}}, {{2}}… as placeholders — Meta templates go by position, not by name.',
    exampleHint: 'Sample values for Meta’s reviewers, separated by commas.',
    badName: 'Template names can only use small letters, digits and underscores.',
    writeBody: 'Write the template body first lah.',
    bindingsUpdated: '“{name}” bindings updated — no need for Meta to review again.',
    updated: '“{name}” updated liao.',
    updatedReview: '“{name}” updated — Meta will check the edits again before they go live.',
    savedSandbox: '“{name}” saved (sandbox — can use right away).',
    submitted: '“{name}” sent to Meta for review — status now {status}.',
    deleted: '“{name}” deleted already.',
    deleteTitle: 'Confirm delete {name} ah?',
    deleteBody: 'Every language version of this template also kena removed.',
  },
  variables: {
    builtins: 'Click to pop in a variable — it fills from the contact’s CRM record when sent:',
    tags: 'Tags (Library → Tags) — a tag’s fields; stays empty if the contact or its entity don’t have that tag:',
  },
}
