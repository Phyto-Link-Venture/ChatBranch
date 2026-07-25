/**
 * Back-compat shim: role/header helpers now come from the real auth layer
 * (lib/auth.ts — bearer tokens + server-side RBAC). Existing imports of
 * getRole/roleHeaders keep working.
 */
export { getRole, authHeaders as roleHeaders, type Role } from './auth'
