"""Optional, isolated Qwen3.5 text chat routes for the Molab and Colab TTS apps.

The host notebook embeds this function so a single uploaded notebook remains
self-contained. Do not import or load a text model on the TTS-only path.
"""


def mount_optional_text_llm(app, gpu_slots, *, enabled, api_key):
    import asyncio
    import hmac
    import os
    import time
    import uuid

    from fastapi import Header, HTTPException
    from pydantic import BaseModel, Field

    llm_id = "lukey03/Qwen3.5-9B-abliterated"
    llm_state = {"status": "unloaded", "model": None, "tokenizer": None,
                 "error": None, "available_gpu_bytes": None}
    llm_lock = asyncio.Lock()

    class ChatRequest(BaseModel):
        model: str
        messages: list[dict] = Field(min_length=2, max_length=32)
        max_tokens: int = Field(default=256, ge=1, le=512)
        temperature: float = Field(default=0.0, ge=0.0, le=1.5)
        stream: bool = False

    def authorize_chat(value, bearer):
        if not api_key:
            raise HTTPException(503, detail={"code": "llm_auth_not_configured",
                                             "message": "Set a private LLM API key on the runtime."})
        bearer_key = (bearer[7:] if bearer and bearer[:7].lower() == "bearer " else "")
        supplied = bearer_key or value
        if not supplied or not hmac.compare_digest(supplied, api_key):
            raise HTTPException(401, detail={"code": "invalid_api_key"})

    def validate_chat(request):
        if request.model != llm_id:
            raise HTTPException(422, detail={"code": "model_mismatch", "expected": llm_id})
        if request.stream:
            raise HTTPException(422, detail={"code": "stream_not_supported"})
        messages = request.messages
        if messages[0].get("role") != "system" or not str(messages[0].get("content", "")).strip():
            raise HTTPException(422, detail={"code": "system_message_required"})
        if messages[-1].get("role") != "user":
            raise HTTPException(422, detail={"code": "last_message_must_be_user"})
        for index, item in enumerate(messages):
            if (set(item) != {"role", "content"}
                    or item["role"] not in {"system", "user", "assistant"}
                    or (item["role"] == "system" and index != 0)
                    or not isinstance(item["content"], str)
                    or not item["content"].strip()
                    or len(item["content"]) > 12000):
                raise HTTPException(422, detail={"code": "invalid_chat_messages"})
        return messages

    def load_text_model():
        import torch

        if not torch.cuda.is_available():
            raise RuntimeError("cuda_unavailable: A CUDA GPU is required for this model.")
        free_bytes, _total_bytes = torch.cuda.mem_get_info(0)
        llm_state["available_gpu_bytes"] = free_bytes
        # This public BF16/FP16 checkpoint has ~17.9 GB of weights, before
        # KV cache and the pinned TTS model. Do not silently evict TTS or
        # offload to CPU and then report unusably slow inference as ready.
        if free_bytes < 19 * (1024 ** 3):
            raise RuntimeError(
                f"gpu_memory_insufficient: {free_bytes // (1024 ** 2)} MiB free; "
                "the unquantized 9B checkpoint needs at least 19 GiB free "
                "alongside TTS. Use a larger GPU or a separately hosted LLM."
            )
        try:
            from transformers import AutoModelForCausalLM, AutoTokenizer
            from transformers.models.qwen3_5.modeling_qwen3_5 import Qwen3_5ForCausalLM
        except ImportError as error:
            raise RuntimeError(
                "llm_dependencies_missing: Install a Transformers version "
                "with Qwen3.5 support and accelerate on the opted-in runtime."
            ) from error
        tokenizer = AutoTokenizer.from_pretrained(llm_id, trust_remote_code=False)
        model = AutoModelForCausalLM.from_pretrained(
            llm_id,
            device_map={"": "cuda:0"},
            dtype=torch.bfloat16 if torch.cuda.is_bf16_supported() else torch.float16,
            trust_remote_code=False,
        )
        if not isinstance(model, Qwen3_5ForCausalLM):
            del model
            raise RuntimeError("llm_model_identity_mismatch: Unexpected loaded architecture.")
        model.eval()
        llm_state.update(model=model, tokenizer=tokenizer, error=None, status="ready")

    def generate_chat(messages, max_tokens, temperature):
        import torch

        tokenizer = llm_state["tokenizer"]
        model = llm_state["model"]
        # Preserve the exact system role and text supplied by the VPS persona.
        inputs = tokenizer.apply_chat_template(
            messages, add_generation_prompt=True, tokenize=True,
            return_dict=True, return_tensors="pt",
        ).to(model.device)
        prompt_tokens = inputs["input_ids"].shape[-1]
        if prompt_tokens > 4096:
            raise ValueError("context_too_long: maximum 4096 input tokens")
        with torch.inference_mode():
            generated = model.generate(
                **inputs, max_new_tokens=max_tokens,
                do_sample=temperature > 0,
                **({"temperature": temperature} if temperature > 0 else {}),
                pad_token_id=tokenizer.eos_token_id,
            )
        new_tokens = generated[0][prompt_tokens:]
        return tokenizer.decode(new_tokens, skip_special_tokens=True), prompt_tokens, len(new_tokens)

    @app.get("/v1/health")
    async def optional_llm_health():
        load_state = ("disabled" if not enabled else
                      "configuration_required" if not api_key else llm_state["status"])
        return {"model": llm_id, "enabled": bool(enabled),
                # Only a successfully loaded model is ready; opt-in alone is
                # not evidence that memory, dependencies or weights are usable.
                "status": ("ready" if enabled and api_key and
                           llm_state["status"] == "ready" else "unavailable"),
                "load_state": load_state,
                "loaded": llm_state["model"] is not None,
                "error": llm_state["error"],
                "available_gpu_bytes": llm_state["available_gpu_bytes"],
                "endpoint": "/v1/chat/completions"}

    @app.get("/v1/models")
    async def optional_llm_models(x_api_key: str | None = Header(default=None, alias="X-Api-Key"),
                                  authorization: str | None = Header(default=None, alias="Authorization")):
        authorize_chat(x_api_key, authorization)
        return {"object": "list", "data": ([{"id": llm_id, "object": "model"}]
                if enabled and llm_state["status"] == "ready" else [])}

    @app.post("/v1/chat/completions")
    async def optional_llm_chat(request: ChatRequest,
                                x_api_key: str | None = Header(default=None, alias="X-Api-Key"),
                                authorization: str | None = Header(default=None, alias="Authorization")):
        if not enabled:
            raise HTTPException(503, detail={"code": "llm_disabled",
                                             "message": "Set TSUKI_ENABLE_TEXT_LLM=1 on this runtime."})
        authorize_chat(x_api_key, authorization)
        messages = validate_chat(request)
        async with llm_lock:
            async with gpu_slots:
                if llm_state["model"] is None:
                    llm_state["status"] = "loading"
                    try:
                        await asyncio.to_thread(load_text_model)
                    except Exception as error:
                        # Avoid leaking file paths, tokens, or provider internals.
                        import torch
                        if isinstance(error, torch.cuda.OutOfMemoryError):
                            detail = "gpu_memory_insufficient: CUDA exhausted during LLM loading."
                        elif isinstance(error, RuntimeError) and str(error).split(":", 1)[0] in {
                            "cuda_unavailable", "gpu_memory_insufficient",
                            "llm_dependencies_missing", "llm_model_identity_mismatch"}:
                            detail = str(error)
                        else:
                            detail = f"llm_load_failed: {type(error).__name__}"
                        llm_state.update(status="error", model=None, tokenizer=None, error=detail)
                        torch.cuda.empty_cache()
                        raise HTTPException(503, detail={"code": "llm_unavailable", "message": detail}) from None
                started = time.time()
                try:
                    text, prompt_tokens, completion_tokens = await asyncio.to_thread(
                        generate_chat, messages, request.max_tokens, request.temperature)
                except ValueError as error:
                    if str(error).startswith("context_too_long:"):
                        raise HTTPException(422, detail={"code": "context_too_long"}) from None
                    raise
                except Exception as error:
                    import torch
                    if isinstance(error, torch.cuda.OutOfMemoryError):
                        torch.cuda.empty_cache()
                        raise HTTPException(503, detail={"code": "gpu_memory_insufficient",
                                                         "message": "CUDA exhausted during LLM generation."}) from None
                    raise HTTPException(503, detail={"code": "llm_inference_failed",
                                                     "message": type(error).__name__}) from None
        return {"id": "chatcmpl-" + uuid.uuid4().hex, "object": "chat.completion",
                "created": int(started), "model": llm_id,
                "choices": [{"index": 0, "message": {"role": "assistant", "content": text},
                             "finish_reason": ("length" if completion_tokens >= request.max_tokens else "stop")}],
                "usage": {"prompt_tokens": prompt_tokens,
                          "completion_tokens": completion_tokens,
                          "total_tokens": prompt_tokens + completion_tokens}}

    return llm_state
