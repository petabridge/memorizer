/**
 * Shared utility functions for PostgMem application
 */

/**
 * Safely escapes HTML characters to prevent XSS attacks
 * @param {string} text - The text to escape
 * @returns {string} HTML-escaped text
 */
function escapeHtml(text) {
    if (!text) return '';
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

/**
 * Formats a date string into a localized date format
 * @param {string|Date} dateInput - Date string or Date object
 * @returns {string} Formatted date string
 */
function formatDate(dateInput) {
    if (!dateInput) return 'Unknown';
    const date = new Date(dateInput);
    return date.toLocaleDateString();
}

/**
 * Formats a date string into a localized date and time format
 * @param {string|Date} dateInput - Date string or Date object
 * @returns {string} Formatted date and time string
 */
function formatDateTime(dateInput) {
    if (!dateInput) return 'Unknown';
    const date = new Date(dateInput);
    return date.toLocaleString();
}

/**
 * Creates a formatted tag badge HTML
 * @param {string} tag - The tag text
 * @param {string} badgeClass - Bootstrap badge class (default: 'bg-secondary')
 * @returns {string} HTML for the tag badge
 */
function createTagBadge(tag, badgeClass = 'bg-secondary') {
    return `<span class="badge ${badgeClass} me-1">${escapeHtml(tag)}</span>`;
}

/**
 * Creates multiple tag badges from an array
 * @param {string[]} tags - Array of tag strings
 * @param {string} badgeClass - Bootstrap badge class (default: 'bg-secondary')
 * @returns {string} HTML for all tag badges
 */
function createTagBadges(tags, badgeClass = 'bg-secondary') {
    if (!tags || !Array.isArray(tags)) return '';
    return tags.map(tag => createTagBadge(tag, badgeClass)).join('');
}

/**
 * Creates a clickable tag badge that links to the tag filter page
 * @param {string} tag - The tag text
 * @param {string} badgeClass - Bootstrap badge class (default: 'bg-secondary')
 * @returns {string} HTML for the clickable tag badge
 */
function createClickableTagBadge(tag, badgeClass = 'bg-secondary') {
    const href = `/memories?tag=${encodeURIComponent(tag)}`;
    return `<a href="${href}" class="badge ${badgeClass} me-1 text-decoration-none">${escapeHtml(tag)}</a>`;
}

/**
 * Creates multiple clickable tag badges from an array
 * @param {string[]} tags - Array of tag strings
 * @param {string} badgeClass - Bootstrap badge class (default: 'bg-secondary')
 * @returns {string} HTML for all clickable tag badges
 */
function createClickableTagBadges(tags, badgeClass = 'bg-secondary') {
    if (!tags || !Array.isArray(tags)) return '';
    return tags.map(tag => createClickableTagBadge(tag, badgeClass)).join('');
}

/**
 * Truncates text to a specified length with ellipsis
 * @param {string} text - Text to truncate
 * @param {number} maxLength - Maximum length before truncation
 * @returns {string} Truncated text with ellipsis if needed
 */
function truncateText(text, maxLength = 100) {
    if (!text) return '';
    if (text.length <= maxLength) return text;
    return text.substring(0, maxLength) + '...';
}

/**
 * Parses comma-separated tags from a string
 * @param {string} tagsString - Comma-separated tag string
 * @returns {string[]} Array of trimmed tag strings
 */
function parseTagsFromString(tagsString) {
    if (!tagsString) return [];
    return tagsString
        .split(',')
        .map(tag => tag.trim())
        .filter(tag => tag.length > 0);
}

/**
 * Shows a loading state on a button
 * @param {HTMLElement} button - Button element to show loading state
 * @param {string} loadingText - Text to show while loading (default: 'Loading...')
 * @returns {string} Original button text for restoration
 */
function setButtonLoading(button, loadingText = 'Loading...') {
    const originalText = button.innerHTML;
    button.innerHTML = `<i class="fas fa-spinner fa-spin me-2"></i>${loadingText}`;
    button.disabled = true;
    return originalText;
}

/**
 * Restores a button from loading state
 * @param {HTMLElement} button - Button element to restore
 * @param {string} originalText - Original button text to restore
 */
function restoreButton(button, originalText) {
    button.innerHTML = originalText;
    button.disabled = false;
}

/**
 * Creates a confidence badge with appropriate color coding
 * @param {number} confidence - Confidence value (0.0 to 1.0)
 * @returns {string} HTML for confidence badge
 */
function createConfidenceBadge(confidence) {
    const value = confidence.toFixed(2);
    let badgeClass = 'bg-success'; // High confidence (0.8+)
    
    if (confidence < 0.5) {
        badgeClass = 'bg-danger'; // Low confidence
    } else if (confidence < 0.8) {
        badgeClass = 'bg-warning'; // Medium confidence
    }
    
    return `<span class="badge ${badgeClass}">${value}</span>`;
}

/**
 * Handles API errors with user-friendly messages
 * @param {Response} response - Fetch response object
 * @param {string} defaultMessage - Default error message if response parsing fails
 * @throws {Error} Error with user-friendly message
 */
async function handleApiError(response, defaultMessage = 'An error occurred') {
    try {
        const errorData = await response.json();
        throw new Error(errorData.title || errorData.message || defaultMessage);
    } catch (parseError) {
        if (parseError.message.includes('title') || parseError.message.includes('message')) {
            throw parseError; // Re-throw if it's our intended error
        }
        throw new Error(defaultMessage);
    }
} 