var CowartXunfeiIatLibrary = {
  $CowartXunfeiIat: {
    socket: null,
    stateCallback: 0,
    messageCallback: 0,
    decoder: null,
    encoder: null,

    decodeHeapUtf8: function (pointer, length) {
      if (!CowartXunfeiIat.decoder) {
        CowartXunfeiIat.decoder = new TextDecoder('utf-8', { fatal: true });
      }

      return CowartXunfeiIat.decoder.decode(
        HEAPU8.subarray(pointer, pointer + length)
      );
    },

    allocateUtf8: function (value) {
      if (!CowartXunfeiIat.encoder) {
        CowartXunfeiIat.encoder = new TextEncoder();
      }

      var bytes = CowartXunfeiIat.encoder.encode(value || '');
      var pointer = _malloc(Math.max(bytes.length, 1));
      if (!pointer) {
        throw new Error('Failed to allocate Xunfei callback memory.');
      }

      if (bytes.length > 0) {
        HEAPU8.set(bytes, pointer);
      }

      return { pointer: pointer, length: bytes.length };
    },

    notifyState: function (state, message) {
      if (!CowartXunfeiIat.stateCallback) {
        return;
      }

      var encoded = CowartXunfeiIat.allocateUtf8(message || '');
      try {
        {{{ makeDynCall('viii', 'CowartXunfeiIat.stateCallback') }}}(
          state,
          encoded.pointer,
          encoded.length
        );
      } finally {
        _free(encoded.pointer);
      }
    },

    notifyMessage: function (message) {
      if (!CowartXunfeiIat.messageCallback) {
        return;
      }

      var encoded = CowartXunfeiIat.allocateUtf8(message || '');
      try {
        {{{ makeDynCall('vii', 'CowartXunfeiIat.messageCallback') }}}(
          encoded.pointer,
          encoded.length
        );
      } finally {
        _free(encoded.pointer);
      }
    },

    close: function () {
      var socket = CowartXunfeiIat.socket;
      CowartXunfeiIat.socket = null;
      if (socket) {
        socket.onopen = null;
        socket.onmessage = null;
        socket.onerror = null;
        socket.onclose = null;
        try {
          socket.close(1000, 'Unity closed the recognition session.');
        } catch (error) {
        }
      }
    }
  },

  CowartXunfeiIat_RegisterCallbacks__deps: ['$CowartXunfeiIat'],
  CowartXunfeiIat_RegisterCallbacks: function (stateCallback, messageCallback) {
    CowartXunfeiIat.stateCallback = stateCallback;
    CowartXunfeiIat.messageCallback = messageCallback;
  },

  CowartXunfeiIat_Open__deps: ['$CowartXunfeiIat'],
  CowartXunfeiIat_Open: function (urlPointer, urlLength) {
    CowartXunfeiIat.close();

    var url;
    try {
      url = CowartXunfeiIat.decodeHeapUtf8(urlPointer, urlLength);
      var socket = new WebSocket(url);
      CowartXunfeiIat.socket = socket;
      socket.onopen = function () {
        if (CowartXunfeiIat.socket === socket) {
          CowartXunfeiIat.notifyState(0, '');
        }
      };
      socket.onmessage = function (event) {
        if (CowartXunfeiIat.socket === socket) {
          CowartXunfeiIat.notifyMessage(String(event.data || ''));
        }
      };
      socket.onerror = function () {
        if (CowartXunfeiIat.socket === socket) {
          CowartXunfeiIat.notifyState(1, 'Browser WebSocket connection failed.');
        }
      };
      socket.onclose = function (event) {
        if (CowartXunfeiIat.socket === socket) {
          CowartXunfeiIat.socket = null;
          CowartXunfeiIat.notifyState(
            2,
            'WebSocket closed. code=' + event.code + ', reason=' + (event.reason || '')
          );
        }
      };
      return 1;
    } catch (error) {
      CowartXunfeiIat.close();
      CowartXunfeiIat.notifyState(1, 'Failed to open browser WebSocket: ' + String(error));
      return 0;
    }
  },

  CowartXunfeiIat_Send__deps: ['$CowartXunfeiIat'],
  CowartXunfeiIat_Send: function (messagePointer, messageLength) {
    var socket = CowartXunfeiIat.socket;
    if (!socket || socket.readyState !== WebSocket.OPEN) {
      return 0;
    }

    try {
      socket.send(CowartXunfeiIat.decodeHeapUtf8(messagePointer, messageLength));
      return 1;
    } catch (error) {
      CowartXunfeiIat.notifyState(1, 'Browser WebSocket send failed: ' + String(error));
      return 0;
    }
  },

  CowartXunfeiIat_Close__deps: ['$CowartXunfeiIat'],
  CowartXunfeiIat_Close: function () {
    CowartXunfeiIat.close();
  }
};

mergeInto(LibraryManager.library, CowartXunfeiIatLibrary);
