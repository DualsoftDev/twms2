window.authSession = {
    set: () => document.cookie = "authSession=1; path=/; SameSite=Strict",
    check: () => document.cookie.includes("authSession="),
    clear: () => document.cookie = "authSession=; path=/; expires=Thu, 01 Jan 1970 00:00:00 GMT"
};
