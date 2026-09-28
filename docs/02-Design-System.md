# AutoTest AI — UI / UX Design System

**Project:** AutoTest AI  
**Design Source:** Supplied AutoTest AI HTML design reference  
**Design Role:** Visual and interaction source of truth  
**Application Type:** Desktop-first responsive web application

> This document captures the supplied design theme. It does not replace functional requirements from the BRD.

---

# 1. Design Direction

AutoTest AI uses a modern enterprise QA/developer-tool aesthetic:

- Clean, information-dense dashboards
- White/slate application surfaces
- Dark navigation/sidebar
- Indigo as the primary brand/action color
- Purple as an AI accent
- Emerald for success/healthy states
- Rose/red for failures/critical issues
- Amber for warnings/high-priority states
- Monospace typography for logs, technical identifiers, and execution details
- Rounded cards with subtle borders and shadows
- Responsive layout with mobile navigation behavior

The design source describes the product as **“AutoTest AI — Complete Quality Engineering Suite”** and brands it as a **“Smart Quality Suite.”**

---

# 2. Typography

### Primary font

**Inter**

Used for:

- Page headings
- Body text
- Navigation
- Labels
- Buttons
- Dashboard content

### Monospace font

**JetBrains Mono**

Used for:

- Test identifiers
- Code
- Execution logs
- Technical metadata
- Version/status indicators where appropriate

The supplied design explicitly defines Inter and JetBrains Mono.

---

# 3. Brand Color Tokens

The supplied design defines the primary brand palette around indigo:

| Token | Value | Usage |
|---|---|---|
| brand-50 | `#f5f3ff` | Very light brand surfaces |
| brand-100 | `#ede9fe` | Light brand backgrounds |
| brand-500 | `#6366f1` | Primary brand |
| brand-600 | `#4f46e5` | Primary actions |
| brand-700 | `#4338ca` | Hover/strong brand |
| brand-900 | `#1e1b4b` | Deep brand |

Supporting semantic colors used by the supplied design include:

- Slate: application text/background/border system
- Emerald: success/active/healthy
- Rose: error/critical/failure
- Amber: warning/high priority
- Purple: AI/AI-assisted features
- Sky: low severity/secondary informational states

Implementation should centralize these tokens rather than scattering raw values across components.

---

# 4. Application Surfaces

### Main application background

Light slate background approximately equivalent to `slate-50`.

### Cards

- White background
- Thin slate border
- Rounded corners
- Subtle shadow
- Compact but readable internal spacing

### Sidebar

- Dark slate background
- White brand identity
- Muted slate navigation text
- Indigo active navigation state
- Clear system/module grouping

### Header

- White background
- Bottom border
- Project context selector
- Global test-run action
- Notifications
- User profile/logout

---

# 5. Authentication Experience

The supplied design uses a dedicated dark authentication experience:

- Full-screen dark navy/slate background
- Radial indigo/purple glows
- Glassmorphism panel
- Large rounded container
- Split layout on desktop
- Hero/value proposition on the left
- Login/request-access form on the right
- Email/password authentication
- SSO entry points
- Password visibility control
- Forgot-password action
- Request-access flow

The design includes messaging around:

- AI-driven autonomous test automation
- Zero-manual setup
- Self-healing locators
- Automated Jira bug tracking

These are visual/product messaging elements and must not be interpreted as implemented capabilities until mapped to BRD scope.

---

# 6. Global Layout

```text
┌──────────────────────────────────────────────────────────┐
│ Header: menu | project context | run | notifications | user │
├──────────────┬───────────────────────────────────────────┤
│              │                                           │
│ Dark         │ Main content                              │
│ Sidebar      │                                           │
│              │ Cards / tables / charts / workflows       │
│ Modules      │                                           │
│              │                                           │
│ System       │                                           │
└──────────────┴───────────────────────────────────────────┘
```

The desktop sidebar is approximately 256px in the supplied design and collapses/overlays on smaller screens.

---

# 7. Primary Navigation

The supplied design contains these visible modules:

1. Dashboard
2. Test Cases
3. Projects
4. Test Execution
5. Bugs
6. Tickets
7. Reports
8. Settings

The navigation also shows contextual counts/status indicators where appropriate.

BRD modules not directly visible in the initial navigation should be incorporated into the appropriate product areas without breaking the established information architecture.

---

# 8. Dashboard Design

The dashboard is the primary operational overview.

### Header area

- “System Overview” heading
- Supporting description
- Last execution indicator

### KPI cards

The supplied design demonstrates four key KPI cards:

- Total Test Cases
- Passed Tests
- Failed / Bugs
- Tickets Raised

### Analytical widgets

The reference includes:

- Bug Summary by Severity donut
- Test Execution Status/pass-rate gauge
- Integrations Pipeline quick view
- Trend/quality information

The real implementation must replace hard-coded values with API-backed data.

---

# 9. Test Cases Repository

Design characteristics:

- Page title and supporting description
- Prominent AI Case Generator action
- Search
- Framework filter
- Data table
- Test ID
- Test title
- Module
- Framework
- Priority
- Status
- Actions

The repository should later support the richer BRD tagging/filtering requirements without changing the overall visual language.

---

# 10. Projects

The reference uses project cards containing:

- Platform/application icon
- Active status
- Project name
- Description
- Repository information
- Target URL/environment information
- Add Project action

Projects are the primary context for test assets and execution.

---

# 11. Test Execution Console

The execution view uses a dark terminal-style panel.

Design requirements:

- Live status indicator
- Runner/framework information
- Monospace output
- Scrollable logs
- Start pipeline action
- Stop execution action
- Worker/thread status

This view should eventually consume real-time execution events through WebSocket/SignalR rather than simulated client-side logs.

---

# 12. Bugs & Root-Cause View

The reference labels this area:

**Detected Bugs & Root Causes**

Supporting context:

- AI-analyzed failures
- Log traces
- DOM-state screenshots
- Root-cause information

The final implementation should provide expandable evidence and clear distinction between:

- Test failure
- Suspected application defect
- Environment failure
- Automation failure
- AI inference/confidence

---

# 13. Ticket Sync

The supplied UI provides a unified ticket table with:

- Ticket ID
- Platform
- Issue title
- Severity
- Sync status

Supported integrations in the BRD include Jira and Azure DevOps.

---

# 14. Reports & Analytics

The design reference includes:

- Historical pass/fail trend
- Flakiness index summary
- Quality metrics

The BRD expands this into a larger analytics capability including defect density, execution duration, root-cause distribution, SLA tracking, release readiness, and automation coverage.

---

# 15. Settings

Settings should follow the same card-based design and become the home for configuration areas such as:

- Profile
- Workspace/project configuration
- Users/roles
- Integrations
- AI providers/models
- Execution settings
- Environment/secrets
- Notifications
- Audit/security configuration

The exact information architecture should be refined during implementation, but the visual system must remain consistent.

---

# 16. Component Rules

### Buttons

- Primary: indigo
- AI action: indigo → purple gradient may be used where the design calls for an AI-specific action
- Success/start: emerald
- Destructive/stop: rose
- Secondary: slate/white with border

### Status badges

Use semantic colors consistently:

- Active/Passed/Healthy → emerald
- Critical/Failed → rose
- Warning/High → amber
- AI → purple/indigo
- Informational → sky
- Neutral → slate

### Cards

Use consistent:

- rounded corners
- 1px border
- subtle shadow
- compact spacing
- strong heading + muted supporting text

### Tables

Tables should prioritize:

- readable density
- clear column headers
- hover states
- row actions
- status badges
- responsive overflow

---

# 17. Responsive Requirements

The supplied design is responsive.

### Desktop

- Persistent sidebar
- Multi-column dashboards
- Full tables
- Rich execution console

### Tablet

- Collapsible sidebar
- Reduced dashboard columns
- Horizontal table scrolling where required

### Mobile

- Sidebar becomes an overlay/drawer
- Header controls compress
- Cards stack
- Tables use horizontal scrolling or responsive row/card representations
- Execution logs remain usable with monospace scrolling

---

# 18. Accessibility

The visual implementation should additionally enforce:

- Keyboard navigation
- Visible focus states
- Accessible labels
- Semantic buttons/links
- Sufficient contrast
- Screen-reader-friendly status updates
- No color-only meaning

These are implementation quality requirements to preserve the visual intent without sacrificing usability.

---

# 19. Design Non-Negotiables

1. Do not introduce Angular Material/MUI/other unrelated visual systems into the design.
2. Do not replace the supplied indigo/slate visual identity without approval.
3. Do not use arbitrary colors for statuses.
4. Do not turn the dashboard into a generic admin template.
5. Preserve the distinction between normal application UI and developer-oriented terminal/code surfaces.
6. Keep AI actions visually identifiable but not visually overwhelming.
7. All hard-coded demo values must be replaced with real data once backend modules exist.

