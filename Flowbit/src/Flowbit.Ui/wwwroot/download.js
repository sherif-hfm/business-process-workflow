window.flowbit = window.flowbit || {};

window.flowbit.scrollToSection = (sectionId) => {
    const section = document.getElementById(sectionId);
    if (!section) {
        return;
    }

    const prefersReducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    section.scrollIntoView({
        behavior: prefersReducedMotion ? "auto" : "smooth",
        block: "start"
    });

    const hash = `#${sectionId}`;
    if (window.location.hash !== hash) {
        window.history.pushState(null, "", hash);
    }
};

window.flowbit.downloadFileFromStream = async (fileName, contentStreamReference) => {
    const arrayBuffer = await contentStreamReference.arrayBuffer();
    const blob = new Blob([arrayBuffer], { type: "application/json;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");

    anchor.href = url;
    anchor.download = fileName;
    anchor.style.display = "none";
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
};
