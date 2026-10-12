// Manglish overlay for the `admin` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const admin: Overlay<Messages['admin']> = {
  permissions: {
    list: {
      summary_one: '{count} role · click one to set what it can do',
      summary_other: '{count} roles · click one to set what it can do',
      deleteBlocked_one: 'Still got {count} user on this role — reassign first, then only can delete',
      deleteBlocked_other: 'Still got {count} users on this role — reassign first, then only can delete',
    },
    manage: {
      subtitle: 'What {role} can do in {area} — switch each one on or off, then save.',
    },
    form: {
      addSubtitle: 'A new role starts with zero access. Give it capabilities in the card grid, then save.',
      renameSubtitle: 'Give this custom role a nicer, clearer name. The slug and capabilities won’t change.',
      addHint: 'We make a url-safe slug from the name for you. For assignment, custom roles count as agent-level.',
      renameHint: 'Only the display name changes — nothing else kena touched.',
    },
    grid: {
      hint: 'Give this role access, one area at a time',
      noMatch: 'Nothing matches “{query}” leh.',
    },
    page: {
      subtitleRole: 'Give a role access, one resource at a time',
      subtitleList: 'All the roles, and what each one can do',
      loadError:
        'Aiyo, cannot load the permission matrix — {error}. This editable RBAC page needs the backend running (GET/PUT /api/auth/permissions).',
      saved: 'Permissions saved liao.',
      renamed: 'Done — the role is now called “{name}”.',
      created: 'Role “{name}” created — open it, give it access, then save ah.',
      assignedBlock_one: '“{name}” still got {count} user on it — move them to another role first, then only delete.',
      assignedBlock_other: '“{name}” still got {count} users on it — move them to another role first, then only delete.',
      confirmDelete: 'Confirm delete the custom role “{name}”? Once gone, cannot undo.',
      stillAssigned: '“{name}” still got people on it — reassign them first lah.',
      stillAssignedN_one: '“{name}” still got {count} user on it — reassign them first lah.',
      stillAssignedN_other: '“{name}” still got {count} users on it — reassign them first lah.',
      deleted: 'Okay, role “{name}” deleted.',
      superadminOnly:
        'Only people allowed to edit the permission matrix can change it here (rbac.manage — by default that’s the super admins).',
      loading: 'Loading permissions, wait ah…',
      footnote:
        'Access controls the {action}; entity and ownership scoping still apply on top (an admin only manages their own organization’s users — or, for a company admin with entities picked on /users, only those companies’ — and so on). Super admin always keeps {cap} locked on, so nobody can kena locked out completely. Custom roles start with no access — give them capabilities, then {save}.',
    },
  },
  bindings: {
    page: {
      subtitle: 'Who sees which entity, who sits in which team, what got tagged — all in one place',
    },
    groups: {
      mainHint: 'Main entity — everything under it also included',
    },
    entityGrid: {
      empty: 'No entities yet leh — go create one under Entities.',
    },
    kinds: {
      userEntities: {
        hint: 'Which entities this login can see. Tick a main entity and everything under it comes along. None ticked = the whole organization.',
      },
      teamUsers: {
        hint: 'Who’s in this team. New conversations take turns going to each member.',
      },
      contactEntities: {
        hint: 'Which entities this customer belongs to. If the contact sits on a sub-entity, its main entity can also see them.',
      },
      tagContacts: {
        hint: 'Which contacts got this tag. If the tag is in a group, putting it on replaces the contact’s other tag from the same group.',
      },
      tagEntities: {
        hint: 'Which entities got this tag (like inner company / outside company).',
      },
      entityAccounts: {
        hint: 'The numbers and inboxes that belong to this entity. Messages they receive get filed under it. An account can only belong to one entity at a time — bind it here and it moves over.',
      },
    },
    confirmDiscard: 'Throw away your unsaved changes ah?',
    saved: 'All settled — {added} bound, {removed} unbound.',
    unavailable: 'Cannot load bindings now — {error}',
    intro:
      'First choose what you want to bind, pick the one thing on the left, then switch the other side on or off — a whole card at once with its checkbox, or one by one under {manage}. Nothing changes until you save, and saving only sends what you changed.',
    noTargets: 'No {things} yet leh.',
    noContactTags: 'No tags yet — go Library → Tags and create a contact tag first.',
    noEntityTags: 'No tags yet — go Library → Tags and create an entity tag first.',
    nothingToBind: 'Got no {things} to bind yet.',
  },
  tags: {
    page: {
      subtitle: 'What a contact, entity or conversation is — plus the details each one carries',
    },
    kinds: {
      contact: {
        blurb: 'Who this person is to you — boss, staff, driver. Flows, automations and the AI can all read them.',
      },
      entity: {
        blurb: 'What type of company or unit this entity is — inner company, outside company.',
      },
      conversation: {
        blurb: 'Labels the team sticks on chats in the inbox — follow-up, urgent, resolved.',
      },
    },
    manager: {
      intro:
        'Tags tell you what something {is} — a contact who is a {driver}, an entity that is an {outside}, a chat that needs {followUp}. Put tags in a {group} so only one of them can apply at a time, and give a tag {fields} when each use needs its own details (like a driver’s licence number). Flows, automations, templates and the AI all can read them.',
      loadError: 'Cannot load tags lah — {error}',
      loading: 'Loading tags, wait ah…',
      confirmDelete: 'Confirm delete the tag “{name}”?',
      confirmForce: '{reason}\n\nStill want to delete it?',
      deleted: '“{name}” deleted liao.',
      savedTag: 'Done, “{name}” saved.',
      createdTag: '“{name}” created — can use already.',
      noMatch: 'Nothing matches your search leh.',
      noneYet: 'Got no {noun} tags yet.',
      noneYetCreate: 'Got no {noun} tags yet. {create} lah.',
      conversationHint: 'Conversation tags you put on from the inbox',
      seeWhich: 'See and change which {things} got this tag',
    },
    editor: {
      needName: 'Give the tag a name first lah.',
      fieldNeedsLabel: 'Field #{n} still needs a label.',
      subtitle: 'A tag says what something is. Add fields if each use needs its own details.',
      lockedHint_one: 'Already used on {count} — take it off them first, then only can change this.',
      lockedHint_other: 'Already used on {count} — take it off them first, then only can change this.',
      groupHint: 'Tags in the same group cannot mix: each target holds only one of them (e.g. “Company type”: Inner / Outside).',
      belongsHint: 'An entity’s own tag only shows up there; organization-wide tags show up everywhere.',
      fieldsHint: 'Details every {noun} with this tag carries — can use them in templates as {variable}.',
      noFields: 'No fields — this tag is just a label only.',
    },
    assign: {
      noneContact: 'This contact got no tags yet.',
      noneEntity: 'This entity got no tags yet.',
      allApplied: 'All tags already applied liao',
      exclusiveContact: 'Tags in the same group cannot mix — pick one and it replaces the contact’s other tag from that group.',
      exclusiveEntity: 'Tags in the same group cannot mix — pick one and it replaces the entity’s other tag from that group.',
    },
    binding: {
      saved: 'All settled — {added} tagged, {removed} untagged.',
      replacesGroup: 'Tag here and it replaces each {noun}’s other “{group}” tag.',
      requiredHint_one: 'Newly tagged {things} start with no {fields} — remember to fill it in on each {noun}.',
      requiredHint_other: 'Newly tagged {things} start with no {fields} — remember to fill them in on each {noun}.',
      noContacts: 'No contacts yet leh. {link} first.',
      noEntities: 'Got no entities yet.',
    },
  },
  developers: {
    page: {
      subtitle: 'API keys, AI apps over MCP, and the outside tools the assistant can use.',
    },
    keys: {
      description:
        'Your own keys for scripts and AI apps. A key acts as you — same role, same permissions, same entities — and everything it does gets recorded in Activity under {api} or {mcp}.',
      needsPermission: 'To create keys you need the “Manage API keys” permission — ask your admin ya.',
      loadError: 'Cannot load keys — {error}',
      loading: 'Loading keys, wait ah…',
      emptyTenant: 'Nobody in your tenant got a key yet.',
      emptyMine: 'You don’t have any API keys yet lah.',
      confirmRevoke: 'Confirm revoke “{name}”? Anything using it will stop working straight away.',
      confirmRevokeOther: 'Confirm revoke “{name}” (owned by {owner})? Anything using it will stop working straight away.',
      revoked: '“{name}” revoked liao.',
      createSubtitle: 'Use it in your scripts (Authorization: Bearer …) or to connect an AI app over MCP.',
      onlyOnce: 'This key only shows this one time ah. Copy it somewhere safe — if lost, just revoke it and create a new one.',
      filledBelow:
        'We also filled it into “Connect an AI app” below for this visit, so you can terus copy a ready-made config.',
      readOnlyNote: 'If a key got no “manage” or “send” permission, on the REST API it can only read.',
    },
    connect: {
      description:
        'ChatBranch is also an MCP server. Point Claude, Cursor or any MCP client at it with an API key and the app can search contacts, read and answer conversations, and more — as you, within your own permissions.',
      hintClaudeCode: 'Run this in a terminal. Add --scope user if you want it in every project.',
      hintClaudeDesktop:
        'Go Settings → Developer → Edit Config (claude_desktop_config.json), then restart Claude. Need Node.js for mcp-remote ya.',
      hintCursor: 'Put it in ~/.cursor/mcp.json (or .cursor/mcp.json in a project), then switch it on under Settings → MCP.',
      hintHttp:
        'Works with any MCP client that speaks Streamable HTTP. JSON-RPC 2.0 over POST; answers come back as application/json.',
      serverCheck: '{detail} Paste a key here to test the real connection an app would make.',
      connected_one: 'Connected to {server} · protocol {protocol} · this key can use {count} tool.',
      connected_other: 'Connected to {server} · protocol {protocol} · this key can use {count} tools.',
      failed: 'Cannot connect — {error}',
      replaceKey: 'Swap {placeholder} with a key — create one above and we fill it in for you.',
      loadingTools: 'Getting the tools, hold on…',
    },
    mcp: {
      description:
        'Give the ChatBranch assistant tools from other systems. Read-only tools run terus; anything that changes something will wait for the person to confirm first.',
      saved: '“{name}” saved liao.',
      confirmRemove: 'Confirm remove “{name}”? The assistant will lose its tools straight away.',
      removed: 'Okay, “{name}” removed.',
      readOnly: 'Read-only here — external servers are an admin’s job.',
      loadError: 'Cannot load servers — {error}',
      loading: 'Loading servers, wait ah…',
      empty: 'No external servers yet leh.',
      runTest: 'Run Test first to load its tools.',
      modalSubtitle: 'Can use Streamable HTTP servers (the current MCP transport).',
      tokenNote:
        'If you send a bare token as Authorization, it becomes “Bearer <token>”. We keep it on the server and never show it again.',
      offerHint: 'Switch off and the server stays registered, just not used.',
    },
  },
  users: {
    page: {
      subtitle: 'Operator logins, their roles and which entities they can see',
    },
    roleUpdated: 'Role updated — {user} is now {role}.',
    unbound: 'Unbound — {user} not linked to any contact anymore.',
    bound: 'Bound to {contact} — now {user} signs in as that customer.',
    passwordTooShort: 'Password must be at least 8 characters ah.',
    confirmResetTwoFactor:
      'Reset two-step sign-in for {user}? Their authenticator app and recovery codes will stop working, and all their devices kena signed out. Next time they sign in with just their password (and set it up again if your organization requires it).',
    twoFactorReset: 'Two-step sign-in reset for {user} — they’re signed out everywhere now.',
    passwordTooShortMin: 'Password must be at least {min} characters long lah.',
    passwordCommon: 'That password too common already — pick something harder to guess.',
    passwordIsUsername: 'Password cannot be the same as the username lah.',
    passwordReset: 'Password reset done — {user} kena signed out everywhere.',
    confirmDeactivate: 'Confirm deactivate {user}? They’ll be signed out everywhere and cannot log in anymore.',
    deactivatedMsg: '{user} deactivated liao.',
    reactivatedMsg: '{user} is back — can log in again now.',
    usernameRequired: 'Need a username — type it in yourself (autofilled text doesn’t count one).',
    created: 'User {user} created liao.',
    entityAccessUpdated: 'Done, {user}’s entity access updated.',
    adminsOnly: 'Only admins can manage users ya.',
    unavailable: 'Cannot load user management — is the backend API running or not?',
    adminEntityHint:
      'Leave it empty for an organization-wide admin, or pick some companies to make a company admin who only sees and manages those.',
    adminEntityRequired: 'Pick at least one company lah — only a super admin can make an organization-wide admin.',
    introCompanyAdmin: 'As a company admin, you take care of the agent-level users in your own companies and their branches.',
    intro:
      'Operator logins for this dashboard. Reset a password or deactivate someone and they kena signed out everywhere straight away — and the last active super admin can never be demoted or deactivated.',
    introAdmin: 'As an admin, you can add and look after agent-level users in your own organization.',
    customRolesNote:
      'Custom roles (set up under Permissions) count as agent-level for assignment, and can do whatever the permission matrix gives them.',
    empty: 'No users yet leh.',
    addSubtitle: 'Make a new operator login for this dashboard.',
    usernameHint: 'Login name — cannot clash with anyone else’s.',
    passwordHint: 'Pass it to the user separately, not through here; you can reset it anytime.',
    entityAccessHint:
      'An agent only sees contacts and chats of the entities you tick here — plus everything under them. None ticked = the whole organization. Can change it later with the entity button on their row.',
    resetSubtitle: 'Once you set a new password, the user kena signed out everywhere straight away.',
    entityModalSubtitle:
      'Tick a main entity to give everything under it, or open Manage to pick sub-entities one by one. None ticked = the whole organization.',
    emailHint: 'The welcome email and password links will go here.',
    invite: {
      sendHint:
        'They’ll get their username, the login address and a one-time link to choose their own password (valid {hours} hours). We never email any password.',
      needsEmail: 'Key in their email address first to send a welcome email.',
      unavailable: {
        email_not_configured: 'Welcome emails not set up yet — the System email needs a mailbox to send from first.',
        no_login_address: 'Cannot send welcome emails: the server doesn’t know the dashboard’s address (FRONTEND_URL).',
        localhost_login_address:
          'Cannot send welcome emails: FRONTEND_URL points to localhost, so the link won’t work for anybody else.',
      },
      askSuperadmin: 'Ask a super admin to set it up in Settings → System email ya.',
      sent: 'Welcome email on the way to {email}. The link to choose a password can use until {date}.',
      notSent: 'Alamak, the welcome email didn’t go out: {error} Fix it, then use “Resend invite” on their row.',
      createdNoInvite: 'User {user} created. Pass them their password yourself ya.',
      expired: 'Link expired liao — resend it',
      resent: 'Welcome email sent again to {email} — the old link cannot use anymore.',
      resetLinkHint:
        'We email {email} a link so they can choose a new password themselves (valid {hours} hours). Their current password still works until they change it.',
      resetLinkSent: 'Done — password reset link sent to {email}.',
      noEmailForReset: 'Add an email address first, then only can send them a reset link.',
      errors: {
        no_email: 'This user got no email address — add one first.',
        inactive: 'This user is deactivated — reactivate them first lah.',
        password_already_set: 'This user already chose a password — send a password reset link instead.',
        too_many_invites: 'Too many links sent to this person in the last hour already — try again later ya.',
        send_failed: 'Email didn’t go out: {error}',
        invite_needs_email: 'Key in their email address first to send a welcome email.',
      },
    },
    emailSubtitle: 'Welcome emails and password reset links go here. Change it and any links already sent will be cancelled.',
    emailSaved: 'Okay, email updated for {user}.',
    emailInvalid: 'Hmm, that one doesn’t look like an email address leh.',
    emailTaken: 'Another user is already using that email address.',
  },
  entities: {
    page: {
      subtitle: 'Main entities, and all the branches, outlets and units under them',
    },
    adminsOnly: 'Only admins can manage entities ya.',
    unavailable: 'Cannot load entity management — is the backend API running?',
    tagsNotSaved: 'Entity saved, but the tags didn’t go through: {error}',
    updated: 'Entity {name} updated liao.',
    created: 'Done — entity {name} created.',
    confirmDelete: 'Confirm delete entity “{name}”? Contacts and assignments tied to it might kena affected.',
    deleted: 'Okay, entity {name} deleted.',
    intro:
      'Entities are like a tree. A {main} (your organization, or a company in your group) sits on top; branches, outlets and units sit {beneath} it as sub-entities. A main entity can see everything under it — but never the other way round. Agents only see the entities given to them under Users & roles.',
    empty: 'No entities yet — jom add the first one.',
    modalSubtitle: 'A main entity, or a branch, outlet or unit under one. Pick where it sits in the tree.',
    parentHint:
      'A sub-entity follows the organization of the entity it sits under. Main entities got the MAIN badge in the list.',
    activeHint: 'Inactive entities stay on record, but routing won’t use them anymore.',
    tagsHint: 'What type of entity this is — e.g. inner company or outside company. Manage the list under Library → Tags.',
    noTags: 'No entity tags yet leh.',
  },
  teams: {
    page: {
      subtitle: 'Who owns a conversation, and who it goes to next',
    },
    updated: 'Team {name} updated liao.',
    created: 'Team {name} created — ready to go.',
    confirmDelete: 'Confirm delete team “{name}”?',
    confirmDeleteOpen_one:
      'Confirm delete team “{name}”? Its {count} open conversation goes back to unassigned — nothing lost one.',
    confirmDeleteOpen_other:
      'Confirm delete team “{name}”? Its {count} open conversations go back to unassigned — nothing lost one.',
    deleted: 'Okay, team {name} deleted.',
    unavailable: 'Cannot load teams — is the backend API running?',
    unavailableError: 'Cannot load teams — {error}',
    noMembers: 'No members yet — nobody to assign to leh',
    intro:
      'A conversation belongs to a {team} first, then to one of its agents — so even if nobody picked it up yet, it still got an owner. With {roundRobin} on, each new conversation goes to the next member in turn. Delete a team and its conversations won’t disappear; they just go back to unassigned.',
    empty: 'No teams yet — create one so new conversations got an owner.',
    modalSubtitle: 'Who owns a conversation, and who it goes to next.',
    autoAssignHint: 'Every new conversation goes to the next member, one by one.',
    noUsers: 'No users yet — go create them under Users & roles first.',
  },
  assignments: {
    page: {
      subtitle: 'Which sending account each contact goes through, and the rules behind it.',
    },
    alreadyBalanced: 'Already balanced liao — no contacts moved.',
    rebalanced_one: 'Rebalanced — {count} contact moved. Pinned contacts stayed where they were.',
    rebalanced_other: 'Rebalanced — {count} contacts moved. Pinned contacts stayed where they were.',
    unavailable: 'Cannot load account assignment — {error}',
    intro:
      'Every contact is {sticky} to one sending account per channel, so the conversation always stays on the same number.',
    introPins: 'Pinned contacts never move when you rebalance; but if an account goes down, failover still kicks in.',
    noAccounts: 'No {channel} accounts yet — go link one under Channels.',
    updated_one: '{count} assignment updated — all settled.',
    updated_other: '{count} assignments updated — all settled.',
    confirmTitle: 'Rebalance evenly ah?',
    confirmSubtitle:
      'This spreads every unpinned {channel} contact across the healthy accounts. Pinned contacts won’t move.',
    mayMove_one:
      '{count} unpinned contact might move. Contacts already on an account with space stay there, so only the extra ones change number.',
    mayMove_other:
      '{count} unpinned contacts might move. Contacts already on an account with space stay there, so only the extra ones change number.',
    partialSave: '{count} saved, then got problem: {error}',
    manageSubtitle_one:
      '{count} contact going through this account. Move a contact to another account, or pin it so rebalancing won’t touch it.',
    manageSubtitle_other:
      '{count} contacts going through this account. Move a contact to another account, or pin it so rebalancing won’t touch it.',
    noneRouted: 'No contacts going through this account yet.',
    noMatch: 'No contacts match “{query}” leh.',
    pinnedHint: 'Pinned — rebalancing won’t move it one',
  },
  routing: {
    recovery: {
      isDown: '「{name}」 is down liao',
      affected_one: '— {count} contact kena affected.',
      affected_other: '— {count} contacts kena affected.',
      onTheWay: '{queued} — recovery templates on the way already.',
    },
    saved: 'Routing rules saved — live straight away.',
    subtitle:
      'How reminder (business) and conversation (personal) numbers work together: AI reply pacing, the reminder-number redirect, ban recovery and the cold-text guard.',
    pacing: {
      sub: '{queued} sends one message at a time per account, with human-like delays (recommended). During an account’s quiet hours (Channels → edit account → Delivery & AI), queued replies wait until working hours. {instant} replies straight away.',
    },
    bot: {
      sub: 'Default Chatbot state for reminder (business) numbers. {off}: the business number stays as a template/notification channel only — replies coming in get the redirect below, and the bot only answers chats where you tick Chatbot on. {on}: the bot chats on the business number by default (replies are instant — queued pacing only protects personal numbers). Per-chat Chatbot ticks always win over this default.',
    },
    redirect: {
      sub: 'When a contact replies to a reminder (business) number and the chatbot is off for that chat, we auto-send one message per 24h pointing them to where they can actually chat.',
    },
    target: {
      sub: '{conversation} sends a wa.me link to the contact’s assigned conversation (personal) number. {support} tells them this line is automated, so contact your human support instead. {template} replies with an approved template you pick below.',
    },
    autoReply: {
      sub: 'Sent from the business number itself when a contact replies to it (once per 24h). Leave it on None and the redirect falls back to the wa.me conversation-number handoff.',
    },
    support: {
      sub: 'Goes into the auto-reply so the contact knows where got a human to answer — a phone number, email or link. Leave empty and it sends general wording.',
    },
    ban: {
      sub: 'How recovery starts when a conversation number goes down and still got contacts assigned: one click from the alert/notification, or fully automatic.',
    },
    recoveryTemplate: {
      sub: 'Sent through the business number to affected contacts when a conversation number goes down, pointing them to the replacement number. Approved templates only ya.',
    },
    cold: {
      sub: 'Messaging contacts who never wrote in first is WhatsApp’s number one ban trigger for conversation numbers. Block stops them (recommended), Warn asks you to confirm first, Allow sends anyway — the ban risk is on you ah.',
    },
    optOut: {
      sub: 'If a customer message is exactly one of these, the conversation opts out of AI replies, templates and queued messages (any channel). STOP, UNSUBSCRIBE and BERHENTI always work; add your own here. Organization-wide.',
    },
    optIn: {
      sub: 'Words that opt a conversation back in. START, UNSTOP and MULA always work.',
    },
    needsPermission: 'To touch routing rules, you need the account assignment permission.',
    endpointDown:
      'Cannot reach the routing-rules endpoint — showing the defaults for now; saving will try the live API again.',
  },
}
