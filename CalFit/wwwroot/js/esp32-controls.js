export function bind(root, dotnet) {
    let timer = null;
    let direction = null;
    let pointerId = null;
    let inFlight = false;
    let sequence = 0;
    let disposed = false;

    const cleanups = [];

    function listen(target, eventName, handler) {
        target.addEventListener(eventName, handler);

        cleanups.push(() => {
            target.removeEventListener(eventName, handler);
        });
    }

    async function send(command) {
        await dotnet.invokeMethodAsync(
            "MotorCommand",
            command,
            ++sequence
        );
    }

    async function stop() {
        clearInterval(timer);

        timer = null;
        direction = null;
        pointerId = null;

        try {
            await send("stop");
        } catch {
            // Firmware timeout remains the independent fallback.
        }
    }

    async function renew() {
        if (disposed || !direction || inFlight)
            return;

        inFlight = true;

        try {
            await send(direction);
        } catch {
            await stop();
        } finally {
            inFlight = false;
        }
    }

    const buttons = [...root.querySelectorAll("[data-motor]")];

    for (const button of buttons) {
        listen(button, "pointerdown", event => {
            if (
                disposed ||
                button.disabled ||
                direction ||
                document.hidden ||
                event.button !== 0
            ) {
                return;
            }

            event.preventDefault();

            pointerId = event.pointerId;
            button.setPointerCapture(pointerId);

            direction = button.dataset.motor;

            void renew();
            timer = setInterval(renew, 200);
        });

        listen(button, "lostpointercapture", () => {
            if (direction)
                void stop();
        });

        // Keyboard activation sends a bounded movement pulse.
        listen(button, "keydown", event => {
            if (
                button.disabled ||
                direction ||
                event.repeat ||
                ![" ", "Enter"].includes(event.key)
            ) {
                return;
            }

            event.preventDefault();
            direction = button.dataset.motor;
            void renew();
        });

        listen(button, "keyup", event => {
            if ([" ", "Enter"].includes(event.key))
                void stop();
        });
    }

    listen(window, "pointerup", event => {
        if (direction && event.pointerId === pointerId)
            void stop();
    });

    listen(window, "pointercancel", () => {
        if (direction)
            void stop();
    });

    listen(window, "blur", () => {
        if (direction)
            void stop();
    });

    listen(window, "pagehide", () => {
        if (direction)
            void stop();
    });

    listen(document, "visibilitychange", () => {
        if (document.hidden && direction)
            void stop();
    });

    const observer = new MutationObserver(() => {
        if (
            direction &&
            (!root.isConnected || buttons.some(button => button.disabled))
        ) {
            void stop();
        }
    });

    observer.observe(document.body, {
        childList: true,
        subtree: true,
        attributes: true,
        attributeFilter: ["disabled"]
    });

    return {
        stop,

        async dispose() {
            disposed = true;
            observer.disconnect();

            cleanups.forEach(cleanup => cleanup());

            await stop();
        }
    };
}