/**
 * Sidebar Tags Module
 * Loads global tag counts and renders a collapsible tag navigation section in the sidebar.
 */
const SidebarTags = (function() {
    const STORAGE_KEY = 'memorizer-sidebar-tags-expanded';
    const MAX_TAGS = 30;
    let container = null;
    let isInitialized = false;
    let tagCounts = null;

    function getExpanded() {
        try {
            return localStorage.getItem(STORAGE_KEY) === 'true';
        } catch (e) {
            return false;
        }
    }

    function setExpanded(expanded) {
        try {
            localStorage.setItem(STORAGE_KEY, expanded ? 'true' : 'false');
        } catch (e) {
            // ignore storage errors
        }
    }

    function applyExpanded(expanded) {
        container?.querySelector('.sidebar-tags-toggle')?.classList.toggle('expanded', expanded);
        container?.querySelector('.sidebar-tags-body')?.classList.toggle('expanded', expanded);
        container?.querySelector('.sidebar-tags-header')?.setAttribute('aria-expanded', expanded ? 'true' : 'false');
    }

    function escapeHtml(text) {
        if (!text) return '';
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    function render() {
        const body = container.querySelector('.sidebar-tags-body');
        const total = container.querySelector('#sidebarTagsTotal');
        if (!body) return;

        if (!tagCounts || tagCounts.length === 0) {
            body.innerHTML = '<div class="sidebar-tags-empty">No tags yet</div>';
            if (total) total.style.display = 'none';
            return;
        }

        if (total) {
            total.textContent = tagCounts.length;
            total.style.display = 'inline-block';
        }

        const pills = tagCounts.slice(0, MAX_TAGS).map(t =>
            `<a href="/memories?tag=${encodeURIComponent(t.tag)}" class="sidebar-tags-pill" title="${escapeHtml(t.tag)} (${t.count})">
                ${escapeHtml(t.tag)} <span class="sidebar-tags-pill-count">${t.count}</span>
            </a>`
        ).join('');

        const more = tagCounts.length > MAX_TAGS
            ? `<a href="/memories" class="sidebar-tags-more">View all ${tagCounts.length} tags</a>`
            : '';

        body.innerHTML = `<div class="sidebar-tags-pills">${pills}</div>${more}`;
    }

    async function load() {
        try {
            const response = await fetch('/api/memory/tags/cloud');
            if (!response.ok) throw new Error(`Failed to load tags: ${response.status}`);
            tagCounts = await response.json();
            render();
        } catch (e) {
            console.warn('Failed to load sidebar tags:', e);
            const body = container?.querySelector('.sidebar-tags-body');
            if (body) body.innerHTML = '<div class="sidebar-tags-empty">Failed to load</div>';
        }
    }

    function toggle() {
        const expanded = !getExpanded();
        setExpanded(expanded);
        applyExpanded(expanded);
    }

    function init() {
        if (isInitialized) return;
        container = document.getElementById('sidebar-tags-container');
        if (!container) {
            console.warn('Sidebar tags container not found');
            return;
        }
        isInitialized = true;
        applyExpanded(getExpanded());
        load();
    }

    return {
        init: init,
        toggle: toggle,
        load: load
    };
})();

// Initialize when DOM is ready
document.addEventListener('DOMContentLoaded', function() {
    SidebarTags.init();
});
