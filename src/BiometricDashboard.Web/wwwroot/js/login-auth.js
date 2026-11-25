(function () {
    const pwd = document.getElementById("loginPassword");
    const toggle = document.getElementById("togglePassword");
    if (!pwd || !toggle) return;

    toggle.addEventListener("click", () => {
        const show = pwd.type === "password";
        pwd.type = show ? "text" : "password";
        toggle.innerHTML = show ? '<i class="bi bi-eye-slash"></i>' : '<i class="bi bi-eye"></i>';
        toggle.setAttribute("aria-label", show ? "Hide password" : "Show password");
    });
})();
