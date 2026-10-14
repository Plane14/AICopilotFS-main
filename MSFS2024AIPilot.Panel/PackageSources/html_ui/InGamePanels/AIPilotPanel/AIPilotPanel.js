/**
 * In-sim AI Pilot toolbar panel.
 *
 * Hosted by MSFS's own Coherent/Gameface UI framework: the <ingame-ui> element is
 * defined by the fs-base-ui template, so this file only supplies the panel's own
 * custom element and behaviour. State comes from the out-of-process host EXE over
 * its localhost HTTP bridge (Core/PanelHttpBridge.cs); the panel itself holds no
 * flight state of its own.
 */
class IngamePanelAIPilot extends TemplateElement {
    constructor() {
        super(...arguments);
    }

    connectedCallback() {
        super.connectedCallback();

        try {
            this.m_MainDisplay = document.querySelector("#MainDisplay");
            if (this.m_MainDisplay) {
                this.m_MainDisplay.classList.add("hidden");
            }
            this.m_Footer = document.querySelector("#Footer");
            if (this.m_Footer) {
                this.m_Footer.classList.add("hidden");
            }
        } catch (e) {
            // Optional in the template; ignore when absent.
        }

        this.elControlState = document.getElementById("controlState");
        this.elConnDot = document.getElementById("connDot");
        this.elConnText = document.getElementById("connText");
        this.elPhase = document.getElementById("phaseText");
        this.elChecklist = document.getElementById("checklistLine");
        this.elPushback = document.getElementById("pushbackLine");
        this.elTraffic = document.getElementById("trafficLine");
        this.elWeather = document.getElementById("weatherLine");
        this.elLimits = document.getElementById("limitsLine");
        this.elAltAgl = document.getElementById("altAgl");
        this.elIas = document.getElementById("ias");
        this.elHdg = document.getElementById("hdg");
        this.elVs = document.getElementById("vs");
        this.elWeight = document.getElementById("weight");
        this.elAp = document.getElementById("apState");
        this.elRoute = document.getElementById("routeLine");
        this.elNav = document.getElementById("navLine");
        this.elAtc = document.getElementById("atcLine");
        this.elLog = document.getElementById("atcLog");
        this.m_LastInstruction = "";

        const button = document.getElementById("btnToggle");
        button.addEventListener("click", () => {
            this.sendCommand("toggle");
        });

        // Poll the bridge. 4 Hz is fast enough to read and cheap enough to run in the
        // toolbar without competing with the flight.
        this.m_PollInterval = setInterval(() => this.pollStatus(), 250);
        this.pollStatus();
    }

    disconnectedCallback() {
        super.disconnectedCallback();
        if (this.m_PollInterval) {
            clearInterval(this.m_PollInterval);
            this.m_PollInterval = null;
        }
    }

    initialize() {
    }

    updateImage() {
    }

    async sendCommand(command) {
        try {
            await fetch(`http://localhost:18540/aipilot/${command}`, { method: "POST" });
            this.addLogEntry(`sent: ${command}`);
        } catch (e) {
            this.addLogEntry(`send failed: ${command}`);
        }
        this.pollStatus();
    }

    async pollStatus() {
        try {
            const response = await fetch("http://localhost:18540/aipilot/status");
            if (!response.ok) {
                throw new Error(`HTTP ${response.status}`);
            }
            const status = await response.json();
            this.render(status);
        } catch (e) {
            this.renderOffline();
        }
    }

    renderOffline() {
        if (this.elConnDot) {
            this.elConnDot.classList.remove("is-online");
        }
        if (this.elConnText) {
            this.elConnText.textContent = "Offline";
        }
        if (this.elPhase) {
            this.elPhase.textContent = "Host offline";
        }
    }

    render(status) {
        const connected = status.connected === true;

        if (this.elConnDot) {
            this.elConnDot.classList.toggle("is-online", connected);
        }
        if (this.elConnText) {
            this.elConnText.textContent = connected ? "Online" : "Offline";
        }

        const isAi = status.state === "AiActive";
        if (this.elControlState) {
            this.elControlState.textContent = isAi ? "AI FLYING" : "MANUAL";
            this.elControlState.classList.toggle("is-ai", isAi);
        }
        if (this.elPhase) {
            this.elPhase.textContent = this.formatPhase(status.phase);
        }
        this.renderChecklist(status.checklist);
        this.renderPushback(status.pushback);
        this.renderTraffic(status.traffic);
        this.renderWeather(status.weather);
        this.renderLimits(status.limits);

        const aircraft = status.aircraft || {};
        this.setNumber(this.elAltAgl, aircraft.altitudeAGL, "ft");
        this.setNumber(this.elIas, aircraft.indicatedAirspeed, "kt");
        this.setNumber(this.elWeight, aircraft.grossWeight, "lb");
        if (this.elHdg) {
            this.elHdg.textContent = this.formatHeading(aircraft.heading);
        }
        if (this.elVs) {
            const vs = Number(aircraft.verticalSpeed) || 0;
            this.elVs.textContent = `${vs >= 0 ? "+" : ""}${Math.round(vs)} fpm`;
        }
        if (this.elAp) {
            this.elAp.textContent = aircraft.onGround ? "GND" : "AIR";
        }

        const nav = status.navigation || {};
        if (this.elRoute) {
            const from = nav.departureICAO || "---";
            const to = nav.arrivalICAO || "---";
            const runway = nav.departureRunway ? `/${nav.departureRunway}` : "";
            this.elRoute.textContent = `${from}${runway} -> ${to}`;
        }

        this.renderNavData(status.nav);

        this.renderAtc(status.atc);

        const button = document.getElementById("btnToggle");
        if (button) {
            // new-push-button renders its label from the title attribute, not from
            // textContent, so the visible caption is title.
            button.title = isAi ? "TAKE CONTROL" : "DELEGATE TO AI";
            button.classList.toggle("is-ai", isAi);
        }
    }

    /**
     * Traffic in range, and whether the runway is free. The important line is the runway
     * one: a pilot watching the panel needs to know if the AI is holding short of
     * occupied or actively-used strip, not how many aircraft are nearby.
     */
    renderTraffic(traffic) {
        if (!this.elTraffic) {
            return;
        }

        const t = traffic || {};
        const inRange = Number(t.inRange) || 0;

        this.elTraffic.textContent = inRange === 0
            ? "no traffic in range"
            : `${inRange} in range`;

        const unsafe = t.runwayEntrySafe === false;
        this.elTraffic.classList.toggle("is-warning", unsafe);
        if (unsafe) {
            this.elTraffic.textContent = t.runwayOccupied
                ? "RUNWAY OCCUPIED"
                : "RUNWAY ENTRY UNSAFE";
        }
    }

    /**
     * Wind, derived turbulence, icing and the surface state. This is the line that says
     * why the pilot is flying the speeds and bank limits it is on a bad-weather approach:
     * a stronger crosswind, rougher air, ice on the airframe or a contaminated runway.
     */
    renderWeather(weather) {
        if (!this.elWeather) {
            return;
        }

        const w = weather || {};
        const dir = Number(w.windDirection) || 0;
        const spd = Number(w.windSpeed) || 0;
        const xw = Number(w.crosswind) || 0;
        const turb = String(w.turbulence || "Calm").toUpperCase();
        const surface = String(w.surface || "unknown");

        let text = `Wind ${String(Math.round(dir)).padStart(3, "0")}/${Math.round(spd)}kt, ` +
            `xwind ${xw >= 0 ? "+" : ""}${Math.round(xw)}kt, ${turb}`;
        if (w.surfaceContaminated === true) {
            text += `, ${surface.toUpperCase()} RWY`;
        }
        if (w.icingConditions === true || (Number(w.airframeIcePercent) || 0) > 0) {
            const pct = Number(w.airframeIcePercent) || 0;
            text += pct > 0 ? `, ICE ${Math.round(pct)}%` : ", ICING";
        }
        this.elWeather.textContent = text;

        const warning = w.icingConditions === true || turb === "SEVERE" ||
            w.surfaceContaminated === true;
        this.elWeather.classList.toggle("is-warning", warning);
    }

    /**
     * What the pushback tug is doing. A taxi-out standing still on the stand is doing
     * exactly what it should while a tug is waited for, so this line is the difference
     * between "waiting for the pushback" and an aircraft that will not move - and it says
     * when the tug has been dismissed and the aircraft is about to taxi.
     */
    renderPushback(pushback) {
        if (!this.elPushback) {
            return;
        }

        const p = pushback || {};
        if (p.attached === true) {
            const pushed = Math.round(Number(p.pushedMeters) || 0);
            this.elPushback.textContent = p.releasing === true
                ? `PUSHBACK COMPLETE - releasing the tug after ${pushed}m`
                : `PUSHING BACK - tug attached, ${pushed}m`;
        } else if (p.requested === true) {
            this.elPushback.textContent = "PUSHBACK REQUESTED - waiting for the tug";
        } else if (p.available === true) {
            this.elPushback.textContent = "Pushback available at this stand";
        } else {
            this.elPushback.textContent = "";
        }

        this.elPushback.classList.toggle("is-warning", p.releasing === true);
    }

    /**
     * The aircraft's operating envelope: never-exceed speed, load factor, and the vertical
     * rates the airframe is flown at. Turns red when a limit is being exceeded, which is the
     * one line on this panel that says the aircraft is outside what it is certified
     * to do - an overspeed, an over-g, or a rate beyond the structure.
     */
    renderLimits(limits) {
        if (!this.elLimits) {
            return;
        }

        const l = limits || {};
        const exceeded = l.exceeded === true;

        const maxKt = Number(l.maxAirspeedKt) || 0;
        const g = Number(l.loadFactor) || 0;
        const maxG = Number(l.maxLoadFactorG) || 0;

        let text = exceeded
            ? String(l.breach || "LIMIT EXCEEDED").toUpperCase()
            : "ENVELOPE OK";
        if (maxKt > 0) {
            text += ` | Vmo ${Math.round(maxKt)}`;
        }
        if (maxG > 0) {
            text += ` | ${g.toFixed(1)}/${maxG.toFixed(1)}g`;
        }

        this.elLimits.textContent = text;
        this.elLimits.classList.toggle("is-warning", exceeded);
    }

    /**
     * Which controller is talking (sim ATC or BeyondATC), the station tuned on COM1 and
     * the clearances it has issued. New instructions are appended to the log once each,
     * so the panel mirrors the ATC log without repeating entries on every poll.
     */
    renderAtc(atc) {
        if (!this.elAtc) {
            return;
        }
        const a = atc || {};
        const source = a.source === "BeyondAtc" ? "BeyondATC" : "sim ATC";
        const station = a.stationType || a.station || "no station";
        const frequency = a.stationFrequency ? ` ${a.stationFrequency}` : "";

        const cleared = a.cleared || {};
        const flags = [];
        if (cleared.ifr) flags.push("IFR");
        if (cleared.taxi) flags.push("TAXI");
        if (cleared.takeoff) flags.push("DEP");
        if (cleared.landing) flags.push("LDG");

        this.elAtc.textContent =
            `ATC: ${source} ${station}${frequency}` + (flags.length ? ` [${flags.join(" ")}]` : "");

        const instruction = a.lastInstruction;
        if (instruction && instruction !== this.m_LastInstruction) {
            this.m_LastInstruction = instruction;
            this.addLogEntry(instruction);
        }
    }

    /**
     * The route the pilot is actually following, built from the flight plan and the
     * navigation database: which leg is being flown, the published approach the route ends
     * in, and the published missed approach that answers a go-around. A route whose named
     * fixes the database could not place is warned about, because that is the one way this
     * can be silently wrong.
     */
    renderNavData(nav) {
        if (!this.elNav) {
            return;
        }
        const n = nav || {};
        const unresolved = (n.unresolvedFixes || []).length;

        if (n.directToFix) {
            this.elNav.textContent = `Route: direct to ${n.directToFix}`;
            this.elNav.classList.toggle("is-warning", false);
            return;
        }

        const legs = Number(n.legs) || 0;
        if (legs === 0) {
            this.elNav.textContent = "Route: no geometry yet";
            this.elNav.classList.toggle("is-warning", unresolved > 0);
            return;
        }

        const distance = Number(n.activeLegDistanceMeters) || 0;
        const next = n.activeLeg
            ? `next ${n.activeLeg} (${Math.round(distance / 1852)} NM)`
            : `${legs} legs`;
        const runway = n.approachRunway ? ` ${n.approachRunway}` : "";
        const approach = n.hasPublishedApproach ? ` / ${n.approachType}${runway}` : "";
        const missed = n.hasPublishedMissedApproach ? " + missed" : "";

        this.elNav.textContent = `Route: ${next}${approach}${missed}`;
        // A route with a fix the navigation database could not place is the one way this
        // can be silently wrong, so it is shown as a warning rather than a plain leg count.
        this.elNav.classList.toggle("is-warning", unresolved > 0);
    }

    setNumber(element, value, unit) {
        if (!element) {
            return;
        }
        const number = Number(value);
        element.textContent = Number.isFinite(number)
            ? `${Math.round(number)} ${unit}`
            : "--";
    }

    /**
     * Current checkpoint from the aircraft's own checklist, e.g.
     * "PREFLIGHT_GATE 3/9 PARKING BRAKE SET". Source is "Aircraft" when the
     * installed aircraft ships its own checklists and "Generic" for the
     * built-in stand-in flow.
     */
    renderChecklist(checklist) {
        if (!this.elChecklist) {
            return;
        }
        const cl = checklist || {};
        if (!cl.loaded) {
            this.elChecklist.textContent = "Checklist: none";
            return;
        }
        if (cl.item) {
            this.elChecklist.textContent =
                `Checklist: ${cl.step} ${cl.index}/${cl.count} ${cl.item} ${cl.expectation}`;
            return;
        }
        const done = (cl.completed || []).length;
        const total = (cl.lists || []).length;
        this.elChecklist.textContent =
            total > 0 ? `Checklist: ${done} of ${total} steps done (${cl.source})` : `Checklist: ${cl.source}`;
    }

    formatHeading(heading) {        const number = Number(heading);
        if (!Number.isFinite(number)) {
            return "---";
        }
        return String(Math.round(number) % 360).padStart(3, "0");
    }

    formatPhase(phase) {
        const names = {
            Unknown: "Standby",
            Preflight: "Preflight",
            Startup: "Startup",
            TaxiOut: "Taxi out",
            Takeoff: "Takeoff roll",
            InitialClimb: "Initial climb",
            Climb: "Climb",
            Cruise: "Cruise",
            Descent: "Descent",
            Approach: "Approach",
            FinalApproach: "Final approach",
            Landing: "Landing",
            Rollout: "Rollout",
            TaxiIn: "Taxi in",
            Shutdown: "Shutdown",
            Holding: "Holding",
            Divert: "Divert",
            Emergency: "Emergency",
            GoAround: "Go around"
        };
        return names[phase] || phase || "Standby";
    }

    addLogEntry(text) {
        if (!this.elLog) {
            return;
        }
        const entry = document.createElement("div");
        entry.className = "log-entry";
        entry.textContent = text;
        this.elLog.insertBefore(entry, this.elLog.firstChild);

        // Keep the log short enough for the toolbar.
        while (this.elLog.childElementCount > 40) {
            this.elLog.removeChild(this.elLog.lastElementChild);
        }
    }
}

window.customElements.define("ingamepanel-ai-pilot", IngamePanelAIPilot);
checkAutoload();
