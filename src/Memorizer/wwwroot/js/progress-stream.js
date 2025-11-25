/**
 * ProgressStreamClient - Reusable SSE progress stream client for background job monitoring
 *
 * Provides:
 * - Server-Sent Events connection management
 * - Progress bar and text updates
 * - Completion detection and callback
 * - Error handling with reconnection logic
 * - Results display
 */
class ProgressStreamClient {
    /**
     * Creates a new ProgressStreamClient
     * @param {Object} options - Configuration options
     * @param {string} options.sseUrl - SSE endpoint URL
     * @param {string} options.progressBarId - ID of the progress bar element
     * @param {string} options.progressTextId - ID of the progress text element
     * @param {string} options.progressCardId - ID of the progress card element
     * @param {string} options.resultsCardId - ID of the results card element
     * @param {string} options.resultsContentId - ID of the results content element
     * @param {string} options.startButtonId - ID of the start button element
     * @param {string} options.formId - ID of the form element (for alerts)
     * @param {string} options.jobName - Human-readable job name for messages (e.g., "Title generation")
     * @param {function} [options.onComplete] - Optional callback when job completes
     * @param {function} [options.onError] - Optional callback on connection error
     * @param {function} [options.onProgress] - Optional callback on each progress event
     */
    constructor(options) {
        this.sseUrl = options.sseUrl;
        this.progressBarId = options.progressBarId || 'progressBar';
        this.progressTextId = options.progressTextId || 'progressText';
        this.progressCardId = options.progressCardId || 'progressCard';
        this.resultsCardId = options.resultsCardId || 'resultsCard';
        this.resultsContentId = options.resultsContentId || 'resultsContent';
        this.startButtonId = options.startButtonId || 'startButton';
        this.formId = options.formId;
        this.jobName = options.jobName || 'Job';
        this.onComplete = options.onComplete;
        this.onError = options.onError;
        this.onProgress = options.onProgress;

        this.eventSource = null;
        this.isConnected = false;
    }

    /**
     * Starts listening to the SSE progress stream
     */
    start() {
        // Close any existing connection
        this.stop();

        this.eventSource = new EventSource(this.sseUrl);
        this.isConnected = true;

        this.eventSource.addEventListener('progress', (event) => {
            this._handleProgressEvent(event);
        });

        this.eventSource.onerror = (error) => {
            this._handleError(error);
        };
    }

    /**
     * Stops the SSE connection
     */
    stop() {
        if (this.eventSource) {
            this.eventSource.close();
            this.eventSource = null;
        }
        this.isConnected = false;
    }

    /**
     * Updates the progress bar and text
     * @param {number} percent - Progress percentage (0-100)
     * @param {string} text - Status text to display
     */
    updateProgress(percent, text) {
        const progressBar = document.getElementById(this.progressBarId);
        if (progressBar) {
            progressBar.style.width = percent + '%';
            progressBar.setAttribute('aria-valuenow', percent);
            progressBar.textContent = Math.round(percent) + '%';
        }

        const progressText = document.getElementById(this.progressTextId);
        if (progressText) {
            progressText.textContent = text;
        }
    }

    /**
     * Shows the progress card
     */
    showProgressCard() {
        const progressCard = document.getElementById(this.progressCardId);
        if (progressCard) {
            progressCard.style.display = 'block';
        }
    }

    /**
     * Hides the progress card
     */
    hideProgressCard() {
        const progressCard = document.getElementById(this.progressCardId);
        if (progressCard) {
            progressCard.style.display = 'none';
        }
    }

    /**
     * Shows the results card with content
     * @param {string} content - HTML content to display
     */
    showResults(content) {
        const resultsContent = document.getElementById(this.resultsContentId);
        if (resultsContent) {
            resultsContent.innerHTML = content;
        }

        const resultsCard = document.getElementById(this.resultsCardId);
        if (resultsCard) {
            resultsCard.style.display = 'block';
        }
    }

    /**
     * Enables the start button
     */
    enableStartButton() {
        const startButton = document.getElementById(this.startButtonId);
        if (startButton) {
            startButton.disabled = false;
        }
    }

    /**
     * Disables the start button
     */
    disableStartButton() {
        const startButton = document.getElementById(this.startButtonId);
        if (startButton) {
            startButton.disabled = true;
        }
    }

    /**
     * Shows an alert message
     * @param {string} type - Bootstrap alert type (success, danger, warning, info)
     * @param {string} message - Alert message
     * @param {boolean} [autoDismiss=true] - Auto-dismiss non-danger alerts
     */
    showAlert(type, message, autoDismiss = true) {
        const alertHtml = `
            <div class="alert alert-${type} alert-dismissible fade show" role="alert">
                ${message}
                <button type="button" class="btn-close" data-bs-dismiss="alert" aria-label="Close"></button>
            </div>
        `;

        // Remove existing alerts
        const existingAlerts = document.querySelectorAll('.alert');
        existingAlerts.forEach(alert => alert.remove());

        // Add new alert at the top of the form
        const form = document.getElementById(this.formId);
        if (form) {
            form.insertAdjacentHTML('afterbegin', alertHtml);
        }

        // Auto-dismiss after 5 seconds for non-danger alerts
        if (autoDismiss && type !== 'danger') {
            setTimeout(() => {
                const alert = document.querySelector('.alert');
                if (alert && typeof bootstrap !== 'undefined') {
                    const bsAlert = new bootstrap.Alert(alert);
                    bsAlert.close();
                }
            }, 5000);
        }
    }

    /**
     * Handles a progress event from the SSE stream
     * @private
     */
    _handleProgressEvent(event) {
        const progress = JSON.parse(event.data);
        const percentComplete = progress.percentComplete;
        const totalItems = progress.totalProcessed + progress.outstanding;
        const statusText = `Processing ${progress.totalProcessed} of ${totalItems} items (${progress.totalSuccessful} successful, ${progress.totalFailed} failed)`;

        this.updateProgress(percentComplete, statusText);

        // Call optional progress callback
        if (this.onProgress) {
            this.onProgress(progress);
        }

        // Check if complete - SSE stream will close on completion, but we can also detect it
        if (this._isCompleted(progress)) {
            this._handleCompletion(progress);
        }
    }

    /**
     * Checks if the job is completed
     * @private
     */
    _isCompleted(progress) {
        // Job is complete when:
        // 1. Outstanding is 0 and we've processed at least one item
        // 2. Status indicates completion (Completed, Failed, NoWorkToDo, Idle)
        return (progress.outstanding === 0 && progress.totalProcessed > 0) ||
               progress.status === 'Completed' ||
               progress.status === 'Failed' ||
               progress.status === 'NoWorkToDo' ||
               progress.status === 'Idle';
    }

    /**
     * Handles job completion
     * @private
     */
    _handleCompletion(progress) {
        this.stop();
        this.enableStartButton();

        // Handle different completion scenarios
        if (progress.status === 'NoWorkToDo' || (progress.status === 'Idle' && progress.totalProcessed === 0)) {
            const noWorkMessage = `${this.jobName} - No items to process`;
            this.updateProgress(100, noWorkMessage);
            this.showResults(`<p><strong>No work to do:</strong></p>
                <p>There are no items that need processing at this time.</p>`);
        } else if (progress.status === 'Idle') {
            const idleMessage = `${this.jobName} - No job running`;
            this.updateProgress(0, idleMessage);
            this.hideProgressCard();
        } else {
            const completionMessage = `${this.jobName} completed! ${progress.totalSuccessful} successful, ${progress.totalFailed} failed`;
            this.updateProgress(100, completionMessage);

            const resultsHtml = `<p><strong>Batch completed:</strong></p>
                <ul>
                    <li>Total processed: ${progress.totalProcessed}</li>
                    <li>Successful: ${progress.totalSuccessful}</li>
                    <li>Failed: ${progress.totalFailed}</li>
                    <li>Duration: ${progress.duration?.toFixed(2) || 'N/A'} seconds</li>
                    <li>Requested by: ${progress.requestedBy || 'Unknown'}</li>
                </ul>`;
            this.showResults(resultsHtml);
        }

        // Call optional completion callback
        if (this.onComplete) {
            this.onComplete(progress);
        }
    }

    /**
     * Handles SSE connection error
     * @private
     */
    _handleError(error) {
        console.error('EventSource error:', error);
        this.stop();
        this.showAlert('danger', `Lost connection to progress stream. ${this.jobName} may still be running in the background.`);
        this.enableStartButton();

        // Call optional error callback
        if (this.onError) {
            this.onError(error);
        }
    }

    /**
     * Formats progress details for status display
     * @param {Object} data - Status response data
     * @returns {string} Formatted progress string
     */
    static formatProgressDetails(data) {
        if (!data || !data.isRunning) return '';
        return `(${data.totalProcessed || 0} processed, ${data.totalSuccessful || 0} successful, ${data.totalFailed || 0} failed, ${data.outstanding || 0} remaining)`;
    }
}
