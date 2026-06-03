/**
 * chatHistory — localStorage-backed persistence for Einstein Chat sessions.
 *
 * Storage shape (single key, value is JSON):
 *   einstein-chat:v1 = {
 *       chats: [
 *           {
 *               id:        string,          // uuid
 *               title:     string,          // first user message, trimmed
 *               mode:      'chat'|'agents',
 *               createdAt: number,          // epoch ms
 *               updatedAt: number,
 *               uiMessages: [...],          // exactly what the component renders
 *               history:    [{role, content}, ...]
 *           },
 *           ...
 *       ]
 *   }
 *
 * Per-device, per-browser. No backend, no auth, no PII sent off-device.
 * If localStorage is unavailable (private mode, Locker, quota), every method
 * returns a safe default and isAvailable() is false.
 */

const STORAGE_KEY = 'einstein-chat:v1';
const MAX_CHATS   = 50;            // ring-buffer cap so quota doesn't blow up
const TITLE_LEN   = 60;

const newId = () => {
    if (typeof crypto !== 'undefined' && crypto.randomUUID) return crypto.randomUUID();
    return `chat-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;
};

const safeStorage = () => {
    try {
        if (typeof window === 'undefined' || !window.localStorage) return null;
        // Probe — Safari private mode throws on setItem.
        const probe = `${STORAGE_KEY}:probe`;
        window.localStorage.setItem(probe, '1');
        window.localStorage.removeItem(probe);
        return window.localStorage;
    } catch (e) {
        return null;
    }
};

const readAll = () => {
    const store = safeStorage();
    if (!store) return { chats: [] };
    try {
        const raw = store.getItem(STORAGE_KEY);
        if (!raw) return { chats: [] };
        const parsed = JSON.parse(raw);
        if (!parsed || !Array.isArray(parsed.chats)) return { chats: [] };
        return parsed;
    } catch {
        return { chats: [] };
    }
};

const writeAll = (data) => {
    const store = safeStorage();
    if (!store) return false;
    try {
        store.setItem(STORAGE_KEY, JSON.stringify(data));
        return true;
    } catch (e) {
        // Quota exceeded — drop oldest until it fits.
        if (data.chats.length > 1) {
            data.chats.sort((a, b) => a.updatedAt - b.updatedAt);
            data.chats.shift();
            return writeAll(data);
        }
        return false;
    }
};

const deriveTitle = (uiMessages) => {
    const firstUser = uiMessages?.find?.((m) => m.role === 'user');
    if (!firstUser || !firstUser.text) return 'New chat';
    const t = String(firstUser.text).trim().replace(/\s+/g, ' ');
    return t.length > TITLE_LEN ? `${t.slice(0, TITLE_LEN - 1)}…` : t;
};

export function isAvailable() {
    return safeStorage() !== null;
}

/**
 * Save (or update) a chat. Returns the saved chat record, including its id.
 * If `id` is omitted/null, a new chat is created.
 */
export function saveChat({ id, mode, uiMessages, history }) {
    const data = readAll();
    const now  = Date.now();

    let chat = id ? data.chats.find((c) => c.id === id) : null;
    if (!chat) {
        chat = {
            id:        id || newId(),
            createdAt: now,
            mode:      mode || 'chat',
            uiMessages: [],
            history:    [],
            title:     'New chat',
        };
        data.chats.push(chat);
    }
    chat.mode       = mode || chat.mode;
    chat.uiMessages = Array.isArray(uiMessages) ? uiMessages : [];
    chat.history    = Array.isArray(history) ? history : [];
    chat.updatedAt  = now;
    chat.title      = deriveTitle(chat.uiMessages);

    // Enforce ring-buffer cap by oldest updatedAt.
    if (data.chats.length > MAX_CHATS) {
        data.chats.sort((a, b) => b.updatedAt - a.updatedAt);
        data.chats.length = MAX_CHATS;
    }

    writeAll(data);
    return chat;
}

/**
 * Returns chats sorted by updatedAt descending (most recent first).
 */
export function listChats() {
    const data = readAll();
    return [...data.chats].sort((a, b) => b.updatedAt - a.updatedAt);
}

export function loadChat(id) {
    const data = readAll();
    return data.chats.find((c) => c.id === id) || null;
}

export function deleteChat(id) {
    const data = readAll();
    const before = data.chats.length;
    data.chats = data.chats.filter((c) => c.id !== id);
    if (data.chats.length === before) return false;
    return writeAll(data);
}

export function clearAll() {
    return writeAll({ chats: [] });
}
