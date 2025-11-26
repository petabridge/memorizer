/**
 * Theme Switcher Module
 * Handles dark/light theme toggling with cookie persistence and Prism theme switching.
 */
const ThemeSwitcher = (function() {
    const COOKIE_NAME = 'memorizer-theme';
    const COOKIE_DAYS = 365;
    let isInitialized = false;

    /**
     * Get a cookie value by name
     */
    function getCookie(name) {
        const match = document.cookie.match(new RegExp('(^| )' + name + '=([^;]+)'));
        return match ? match[2] : null;
    }

    /**
     * Set a cookie with the given name, value, and expiration days
     */
    function setCookie(name, value, days) {
        const expires = new Date(Date.now() + days * 864e5).toUTCString();
        document.cookie = `${name}=${value};expires=${expires};path=/;SameSite=Lax`;
    }

    /**
     * Get the current theme from cookie or system preference
     */
    function getTheme() {
        const saved = getCookie(COOKIE_NAME);
        if (saved === 'dark' || saved === 'light') {
            return saved;
        }
        // Fall back to system preference
        if (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches) {
            return 'dark';
        }
        return 'light';
    }

    /**
     * Apply the theme to the document
     */
    function applyTheme(theme) {
        document.documentElement.setAttribute('data-theme', theme);
        updateToggleSwitch(theme);
        updatePrismTheme(theme);

        // Re-render Mermaid diagrams if the module is available
        if (typeof MermaidIntegration !== 'undefined' && typeof MermaidIntegration.updateTheme === 'function') {
            MermaidIntegration.updateTheme(theme);
        }
    }

    /**
     * Update the theme toggle switch state
     */
    function updateToggleSwitch(theme) {
        const themeSwitch = document.getElementById('themeSwitch');
        if (themeSwitch) {
            // Switch is checked when dark mode is active
            themeSwitch.checked = theme === 'dark';
        }
    }

    /**
     * Switch Prism.js syntax highlighting theme
     */
    function updatePrismTheme(theme) {
        const prismLink = document.getElementById('prism-theme');
        if (prismLink) {
            const newHref = theme === 'dark'
                ? 'https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/themes/prism-tomorrow.min.css'
                : 'https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/themes/prism.min.css';

            if (prismLink.href !== newHref) {
                prismLink.href = newHref;
            }
        }
    }

    /**
     * Toggle between light and dark themes
     */
    function toggle() {
        const current = document.documentElement.getAttribute('data-theme') || 'light';
        const newTheme = current === 'dark' ? 'light' : 'dark';
        applyTheme(newTheme);
        setCookie(COOKIE_NAME, newTheme, COOKIE_DAYS);
    }

    /**
     * Initialize the theme on page load
     */
    function init() {
        if (isInitialized) {
            return; // Prevent duplicate initialization which causes flickering
        }
        const theme = getTheme();
        applyTheme(theme);
        isInitialized = true;
    }

    // Public API
    return {
        init: init,
        toggle: toggle,
        getTheme: function() {
            return document.documentElement.getAttribute('data-theme') || 'light';
        }
    };
})();

// Initialize immediately to prevent flash of unstyled content
ThemeSwitcher.init();

// The DOMContentLoaded handler is no longer needed since init() now has
// an initialization guard. The immediate init() call above is sufficient.
// Removing the redundant handler fixes flickering issues with Bootstrap modals.
