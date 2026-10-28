import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Icon } from './Icon.jsx';

/**
 * Three-dot actions menu for a table row (or any inline trigger).
 *
 * The popup is rendered in a portal and positioned with `position: fixed` because table bodies sit
 * inside `.table-wrap` (overflow-x: auto), which would otherwise clip it.
 *
 * Behaviour: click opens, Escape / an outside click / choosing an item close it. Escape and a chosen
 * item return focus to the trigger; an outside click deliberately does not steal focus back from
 * wherever the user clicked. Only one menu is ever open, because another trigger's mousedown is an
 * outside click for the one that is open.
 *
 * items: [{ key, label, onSelect, tone?: 'danger', disabled? }]
 */
export function ActionMenu({ label, items = [] }) {
  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState(null);
  const triggerRef = useRef(null);
  const menuRef = useRef(null);
  const menuId = useId();

  const close = useCallback((restoreFocus) => {
    setOpen(false);
    if (restoreFocus) triggerRef.current?.focus();
  }, []);

  // The menu is fixed-positioned, so it must stay inside the window: below the trigger, or above it when there is no room.
  const place = useCallback(() => {
    const rect = triggerRef.current?.getBoundingClientRect();
    if (!rect) return;
    const height = menuRef.current?.offsetHeight ?? 0;
    const below = rect.bottom + 4;
    const above = rect.top - 4 - height;
    const top = below + height > window.innerHeight - 8 && above >= 8 ? above : below;
    setPos({ top: Math.round(top), right: Math.round(Math.max(8, window.innerWidth - rect.right)) });
  }, []);

  // Anchor the popup under the trigger and move keyboard focus onto the first item.
  useLayoutEffect(() => {
    if (!open) return;
    place();
    menuRef.current?.querySelector('[role="menuitem"]:not([disabled])')?.focus();
  }, [open, place]);

  useEffect(() => {
    if (!open) return undefined;
    const inside = (node) => triggerRef.current?.contains(node) || menuRef.current?.contains(node);
    const onPointerDown = (e) => {
      if (!inside(e.target)) close(false);
    };
    const onKey = (e) => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        close(true);
        return;
      }
      if (e.key === 'Tab') {
        close(false);
        return;
      }
      if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp' && e.key !== 'Home' && e.key !== 'End') return;
      const els = Array.from(menuRef.current?.querySelectorAll('[role="menuitem"]:not([disabled])') || []);
      if (!els.length) return;
      e.preventDefault();
      const at = els.indexOf(document.activeElement);
      const next =
        e.key === 'Home' ? 0 : e.key === 'End' ? els.length - 1 : (at + (e.key === 'ArrowDown' ? 1 : -1) + els.length) % els.length;
      els[next].focus();
    };
    // The popup is anchored to the trigger's rect, so it follows it when the page or the
    // horizontally scrollable table around it moves. (Closing on scroll would be wrong here:
    // focusing the first item can itself scroll an ancestor into view.)
    document.addEventListener('mousedown', onPointerDown, true);
    document.addEventListener('keydown', onKey, true);
    window.addEventListener('resize', place);
    window.addEventListener('scroll', place, true);
    return () => {
      document.removeEventListener('mousedown', onPointerDown, true);
      document.removeEventListener('keydown', onKey, true);
      window.removeEventListener('resize', place);
      window.removeEventListener('scroll', place, true);
    };
  }, [open, close, place]);

  if (!items.length) return null;

  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        className="btn btn--ghost btn--icon btn--sm"
        aria-label={label}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? menuId : undefined}
        onClick={() => setOpen((o) => !o)}
      >
        <Icon name="dots" size={16} />
      </button>
      {open
        ? createPortal(
            <div
              ref={menuRef}
              id={menuId}
              role="menu"
              aria-label={label}
              className="action-menu"
              style={pos ? { top: pos.top, right: pos.right } : { visibility: 'hidden' }}
            >
              {items.map((item) => (
                <button
                  key={item.key}
                  type="button"
                  role="menuitem"
                  tabIndex={-1}
                  disabled={item.disabled}
                  className={`action-menu__item ${item.tone === 'danger' ? 'action-menu__item--danger' : ''}`}
                  onClick={() => {
                    // Focus goes back to the trigger BEFORE the action runs, so a dialog opened by
                    // the item restores focus to the trigger when it closes.
                    close(true);
                    item.onSelect?.();
                  }}
                >
                  {item.label}
                </button>
              ))}
            </div>,
            document.body,
          )
        : null}
    </>
  );
}
