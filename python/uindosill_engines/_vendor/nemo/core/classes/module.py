"""Stub. NeuralModule is nn.Module plus NeMo typing/serialization machinery; the speaker-cache
path uses none of it, so plain nn.Module is a faithful stand-in here."""

import torch.nn as nn


class NeuralModule(nn.Module):
    pass
