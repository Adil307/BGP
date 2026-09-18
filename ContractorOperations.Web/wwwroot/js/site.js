document.addEventListener('DOMContentLoaded', () => {
  const sidebar = document.getElementById('sidebar');
  const toggle = document.getElementById('sidebarToggle');
  if (!sidebar || !toggle) return;

  const desktopMedia = window.matchMedia('(min-width: 901px)');
  const storageKey = 'contractor-ops-sidebar-collapsed';

  const setToggleState = () => {
    if (desktopMedia.matches) {
      const collapsed = document.body.classList.contains('sidebar-collapsed');
      toggle.setAttribute('aria-expanded', (!collapsed).toString());
      toggle.setAttribute('title', collapsed ? 'Expand menu' : 'Collapse menu');
    } else {
      const open = sidebar.classList.contains('open');
      toggle.setAttribute('aria-expanded', open.toString());
      toggle.setAttribute('title', open ? 'Close menu' : 'Open menu');
    }
  };

  // Restore the user's desktop preference without affecting mobile layout.
  if (desktopMedia.matches && localStorage.getItem(storageKey) === '1') {
    document.body.classList.add('sidebar-collapsed');
  }
  setToggleState();

  toggle.addEventListener('click', (event) => {
    event.stopPropagation();

    if (desktopMedia.matches) {
      document.body.classList.toggle('sidebar-collapsed');
      const collapsed = document.body.classList.contains('sidebar-collapsed');
      localStorage.setItem(storageKey, collapsed ? '1' : '0');
    } else {
      sidebar.classList.toggle('open');
    }

    setToggleState();
  });

  document.addEventListener('click', (event) => {
    if (!desktopMedia.matches && sidebar.classList.contains('open') &&
        !sidebar.contains(event.target) && !event.target.closest('#sidebarToggle')) {
      sidebar.classList.remove('open');
      setToggleState();
    }
  });

  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && !desktopMedia.matches && sidebar.classList.contains('open')) {
      sidebar.classList.remove('open');
      setToggleState();
    }
  });

  window.addEventListener('resize', () => {
    if (desktopMedia.matches) {
      sidebar.classList.remove('open');
      document.body.classList.toggle('sidebar-collapsed', localStorage.getItem(storageKey) === '1');
    } else {
      document.body.classList.remove('sidebar-collapsed');
    }
    setToggleState();
  });
});
