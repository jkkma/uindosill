class AudioAugmentor:  # unused by FilterbankFeatures
    def __init__(self, *a, **k): pass
    def perturb(self, *a, **k): pass
    def max_augmentation_length(self, length): return length
    @classmethod
    def from_config(cls, *a, **k): return cls()
