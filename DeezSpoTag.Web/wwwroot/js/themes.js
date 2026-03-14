// DeezSpoTag Default Theme
// Simple theme initialization for the default dark purple theme

const themeStorageKey = 'deezspotag-theme';
const availableThemes = ['blue', 'purple', 'green', 'orange', 'red', 'teal', 'indigo', 'pink', 'amoled-white', 'amoled-red', 'amoled-yellow', 'amoled-blue', 'amoled-neon', 'amoled-green', 'amoled-purple', 'amoled-inverse'];
const themeDisplayNames = {
    blue: 'Blue',
    purple: 'Purple',
    green: 'Green',
    orange: 'Orange',
    red: 'Red',
    teal: 'Teal',
    indigo: 'Indigo',
    pink: 'Pink',
    'amoled-white': 'AMOLED White',
    'amoled-red': 'AMOLED Red',
    'amoled-yellow': 'AMOLED Yellow',
    'amoled-blue': 'AMOLED Blue',
    'amoled-neon': 'AMOLED Neon',
    'amoled-green': 'AMOLED Green',
    'amoled-purple': 'AMOLED Purple',
    'amoled-inverse': 'AMOLED Inverse'
};

document.addEventListener('DOMContentLoaded', () => {
    const savedTheme = getStoredTheme();
    wireThemePicker();

    // Initialize theme variables
    initializeTheme(savedTheme);

    console.log(`DeezSpoTag theme initialized: ${savedTheme}`);
});

function updateMetaThemeColor() {
    let metaTheme = document.querySelector('meta[name="theme-color"]');
    if (!metaTheme) {
        metaTheme = document.createElement('meta');
        metaTheme.name = 'theme-color';
        document.head.appendChild(metaTheme);
    }

    const root = document.documentElement;
    const themeColor = getComputedStyle(root).getPropertyValue('--primary-color').trim() || '#9c27b0';
    metaTheme.content = themeColor;
}

function initializeTheme(theme) {
    const root = document.documentElement;
    const primaryColor = getComputedStyle(root).getPropertyValue('--primary-color').trim();

    if (!primaryColor) {
        console.warn('Theme variables not loaded properly');
    }

    updateMetaThemeColor();

    globalThis.dispatchEvent(new CustomEvent('themeInitialized', {
        detail: { theme, themeName: formatThemeName(theme) }
    }));
}

function getStoredTheme() {
    const stored = localStorage.getItem(themeStorageKey);
    if (stored && availableThemes.includes(stored)) {
        return stored;
    }
    return 'blue';
}

function applyTheme(theme) {
    const className = `theme-${theme}`;
    const root = document.documentElement;

    availableThemes.forEach(name => {
        root.classList.remove(`theme-${name}`);
        document.body?.classList.remove(`theme-${name}`);
    });

    root.classList.add(className);
    document.body?.classList.add(className);
    root.dataset.theme = theme;
    if (document.body) {
        document.body.dataset.theme = theme;
    }
    updateMetaThemeColor();
    syncThemePicker(theme);

    globalThis.dispatchEvent(new CustomEvent('themeChanged', {
        detail: { theme, themeName: formatThemeName(theme) }
    }));
}

function formatThemeName(theme) {
    return themeDisplayNames[theme] || theme.charAt(0).toUpperCase() + theme.slice(1);
}

function wireThemePicker() {
    const menu = document.getElementById('themePickerMenu');
    const toggle = document.getElementById('themePickerToggle');
    const panel = document.getElementById('themePickerPanel');

    const setPickerOpen = (open) => {
        if (!toggle || !panel) {
            return;
        }
        panel.hidden = !open;
        toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
        menu?.classList.toggle('is-open', open);
    };

    if (toggle && panel) {
        setPickerOpen(false);

        toggle.addEventListener('click', (event) => {
            event.preventDefault();
            event.stopPropagation();
            setPickerOpen(panel.hidden);
        });

        panel.addEventListener('click', (event) => {
            event.stopPropagation();
        });

        document.addEventListener('click', (event) => {
            if (!menu || menu.contains(event.target)) {
                return;
            }
            setPickerOpen(false);
        });

        document.addEventListener('keydown', (event) => {
            if (event.key === 'Escape') {
                setPickerOpen(false);
            }
        });
    }

    document.querySelectorAll('.theme-swatch').forEach((swatch) => {
        swatch.addEventListener('click', () => {
            const theme = swatch.dataset.theme;
            if (!theme) {
                return;
            }
            localStorage.setItem(themeStorageKey, theme);
            if (globalThis.UserPrefs) globalThis.UserPrefs.set('theme', theme);
            applyTheme(theme);
            setPickerOpen(false);
        });
    });

    syncThemePicker(getStoredTheme());
}

function syncThemePicker(theme) {
    document.querySelectorAll('.theme-swatch').forEach((swatch) => {
        const isActive = swatch.dataset.theme === theme;
        swatch.classList.toggle('active', isActive);
        swatch.setAttribute('aria-pressed', isActive ? 'true' : 'false');
    });
}

// Export for compatibility with existing code
if (typeof module !== 'undefined' && module.exports) {
    module.exports = {
        getCurrentTheme: () => getStoredTheme(),
        getAvailableThemes: () => availableThemes.reduce((acc, name) => {
            acc[name] = formatThemeName(name);
            return acc;
        }, {}),
        setTheme: (theme) => {
            if (!availableThemes.includes(theme)) {
                return;
            }
            localStorage.setItem(themeStorageKey, theme);
            applyTheme(theme);
        }
    };
}

globalThis.ThemeManager = {
    getCurrentTheme: () => getStoredTheme(),
    getAvailableThemes: () => availableThemes.reduce((acc, name) => {
        acc[name] = formatThemeName(name);
        return acc;
    }, {}),
    setTheme: (theme) => {
        if (!availableThemes.includes(theme)) {
            return;
        }
        localStorage.setItem(themeStorageKey, theme);
        if (globalThis.UserPrefs) globalThis.UserPrefs.set('theme', theme);
        applyTheme(theme);
    }
};
