// Keeps the comment draft in this tab only, so a failed post does not lose the text. Nothing is sent to the server.
(function () {
  var form = document.querySelector("#comments .bdc__form");
  if (!form || !form.elements.contentKey) return;
  var key = "bdc.draft." + form.elements.contentKey.value;
  var fields = ["name", "email", "body"];
  var state = form.getAttribute("data-bdc-state");
  try {
    if (state === "error") {
      var saved = JSON.parse(sessionStorage.getItem(key) || "{}");
      fields.forEach(function (f) {
        var e = form.elements[f];
        if (e && saved[f] && !e.value) e.value = saved[f];
      });
    } else if (state === "sent") {
      sessionStorage.removeItem(key);
    }
    form.addEventListener("input", function () {
      var d = {};
      fields.forEach(function (f) { d[f] = form.elements[f].value; });
      try { sessionStorage.setItem(key, JSON.stringify(d)); } catch (e) { }
    });
  } catch (e) { }
})();
