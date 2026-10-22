# Tailwind and UI guidance applied

Guidance reviewed for this build:

- `tailwind-design-system` (Tailwind v4),
  <https://github.com/wshobson/agents/blob/main/plugins/frontend-mobile-development/skills/tailwind-design-system/SKILL.md>
- `frontend-design`,
  <https://github.com/anthropics/skills/blob/main/skills/frontend-design/SKILL.md>

Applied principles: CSS-first `@theme` tokens, semantic color hierarchy,
reusable Razor components, component variants/sizes/states, native dark-mode
tokens, mobile layouts, visible keyboard focus, semantic forms and notices,
and compiled local Tailwind CSS instead of a runtime/CDN dependency.

The visual reference is Nginx Proxy Manager's orderly management UI. Remote
protocol renderers are isolated in same-origin tool frames, so their global
keyboard handlers do not interfere with the rest of the Blazor dashboard.
