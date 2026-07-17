// ============================================================
// auth - JWT 认证工具：存取 token / 当前用户 / 角色判断 / 登出
// ============================================================

export interface AuthUser {
  token: string
  username: string
  role: 'User' | 'Admin'
  displayName: string
}

const TOKEN_KEY = 'mas_token'
const USER_KEY = 'mas_user'

export function saveAuth(user: AuthUser) {
  localStorage.setItem(TOKEN_KEY, user.token)
  localStorage.setItem(USER_KEY, JSON.stringify(user))
}

export function getToken(): string | null {
  return localStorage.getItem(TOKEN_KEY)
}

export function getCurrentUser(): AuthUser | null {
  const raw = localStorage.getItem(USER_KEY)
  if (!raw) return null
  try { return JSON.parse(raw) as AuthUser } catch { return null }
}

export function logout() {
  localStorage.removeItem(TOKEN_KEY)
  localStorage.removeItem(USER_KEY)
}

export function isAdmin(): boolean {
  return getCurrentUser()?.role === 'Admin'
}

export function isLoggedIn(): boolean {
  return !!getToken()
}
