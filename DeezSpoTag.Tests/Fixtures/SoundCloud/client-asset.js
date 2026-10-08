/* SoundCloud client asset bundle fixture.
   Deliberately contains NO client_id literal. That is the point: a real bundle only reads the
   value through a getter, so the literal the older scraping approach looked for is absent. A
   fixture that did carry one would let the discovery test pass on a fiction. Discovery is
   therefore covered by the apiClient hydration in homepage.html, and these bundles exist to
   prove the bundle fallback finds nothing and discovery still succeeds. */
(function(){
  var __sc = (self.__sc = self.__sc || {});
  __sc_version = {
    build: "1.2.3",
    locale: "en-US",
    apiBase: "https://api-v2.soundcloud.com"
  };
  __sc.app = {
    // This is how a real bundle reaches the id: a read, not a literal.
    getClientId: function(){ return window.__sc_api_client_id || ''; },
    fetchHydration: function(){ return Promise.resolve([]); },
    registerService: function(name, fn){ return fn; }
  };
})();